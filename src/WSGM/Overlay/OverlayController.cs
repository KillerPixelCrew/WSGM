using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Owns the fullscreen overlay's lifetime and summon, gamepad navigation and paired capture/lease
///     transitions, and connects each sheet to the session.
/// </summary>
/// <remarks>
///     Construct, open and dispose on the Avalonia UI thread. Session services are borrowed; window-local
///     subscriptions and input claims are released on close or disposal. Native lease work is asynchronous.
///     The summon triggers live in <see cref="OverlayActivation" />, the pin list in
///     <see cref="QuickAccessPinStore" />, the Windows power state in <see cref="OverlayPowerState" />, the Open
///     apps strip in <see cref="OverlayOpenApps" /> and the per-sheet service connections in
///     <see cref="OverlaySheetServices" />.
/// </remarks>
public sealed partial class OverlayController : IDisposable
{
    private const string QuickAccessSurface = "quick-access";
    private const string SettingsSurface = "settings";

    // The hotkey, chord and edge swipes that summon the sheet, and the controller polling they share.
    private readonly OverlayActivation _activation;
    private readonly AudioProfileService? _audioProfiles;
    private readonly SdFormatManager? _formatManager;

    // Whether the session has a Keep awake owner, which the Wake page's row needs.
    private readonly bool _hasKeepAwake;

    private readonly SessionModes _modes;
    private readonly SteamMonitor? _monitor;
    private readonly OverlayOpenApps _openApps;
    private readonly OverlayPowerState _powerState;
    private readonly bool _previewOnly;

    private readonly OverlaySheetServices _services;

    /// <summary>
    ///     The session's audio manager, shared with the sheet's status pills rather than owned.
    /// </summary>
    private readonly AudioManager _sessionAudio;

    /// <summary>
    ///     The session's removable-drive manager, shared with the sheet's eject pill rather than owned.
    /// </summary>
    /// <remarks>
    ///     Shared for the same reason audio is: Steam's revived storage pages answer while the overlay
    ///     is closed, and a manager the sheet disposes cannot serve them. Two managers would also
    ///     enumerate every volume twice and could disagree about what is still ejectable.
    /// </remarks>
    private readonly RemovableDriveManager _sessionDrives;

    /// <summary>
    ///     The session's radio manager, shared with the sheet's status pills rather than owned.
    /// </summary>
    private readonly RadioManager _sessionRadios;

    /// <summary>The process's Steam Input lease owner this controller claims through.</summary>
    private readonly SteamInputBlocker _steamInput;

    private readonly ConfigStore _store;

    /// <summary>Names of UI surfaces currently holding this controller's shared input claim.</summary>
    private readonly HashSet<string> _uiSurfaces = new(StringComparer.Ordinal);

    private readonly OverlayWindow.SessionState _windowSession = new();

    private AppConfig _config;
    private bool _dialogPriorNavigation;
    private bool _disposed;

    /// <summary>The close refocus still in Steam's raise; reopening the sheet or shutting down cancels it.</summary>
    private CancellationTokenSource? _focusRestoreCancellation;

    private CancellationTokenSource? _keyboardRequestCancellation;

    // The last claim end, which window activation waits for so the game gets its controller back first.
    private Task _leaseRelease = Task.CompletedTask;
    private bool _overlayRequiresSteamLease;
    private OverlayViewModel? _overlayViewModel;
    private string _pendingWarning = "";
    private bool _powerMenuOnly;
    private bool _reopenOverlayForWarning;

    /// <summary>
    ///     Window focused when the overlay opened. Exclusive-fullscreen games
    ///     minimize the moment our panel takes focus — closing the panel calls them
    ///     back (restore + foreground), unless an overlay action redirected focus.
    /// </summary>
    private nint _restoreFocusTo;

    private bool _suppressFocusRestore;
    private SystemStatus? _systemStatus;
    private CancellationTokenSource? _windowReturnCancellation;

    /// <summary>Creates the overlay controller and its input activation surfaces.</summary>
    /// <param name="config">The initial shell configuration.</param>
    /// <param name="store">The persistence owner supplied by the process or resident session.</param>
    /// <param name="steamInput">The process's Steam Input lease owner every surface claims through.</param>
    /// <param name="monitor">The optional Steam lifecycle monitor shared by the shell.</param>
    /// <param name="modes">The session-mode coordinator that performs requested transitions.</param>
    /// <param name="keepAwake">
    ///     The optional session keep-awake service behind the Power
    ///     tab's toggle; null (the Settings preview overlay) hides the row.
    /// </param>
    /// <param name="previewOnly">
    ///     True for a surface that only demonstrates layout and
    ///     input — Settings' "Test sheet" and <c>--overlay-test</c>. It hides
    ///     the desktop/game-mode row and refuses the transition even if it is reached, because
    ///     those processes have no ShellSession, tray host or crash-loop/watchdog recovery:
    ///     one press would exit Explorer and strand the user with no shell.
    /// </param>
    /// <param name="formats">The format manager owned by this composition.</param>
    /// <param name="audio">The composition's audio manager.</param>
    /// <param name="radios">The composition's radio manager.</param>
    /// <param name="drives">The composition's removable-drive manager.</param>
    /// <param name="activationWindow">
    ///     The composition's message window when this controller owns the global reopen triggers
    ///     (hotkey, chord and edge swipe); null for a surface without them, such as the Settings preview.
    /// </param>
    internal OverlayController(AppConfig config, ConfigStore store, SteamInputBlocker steamInput,
        SteamMonitor? monitor, SessionModes modes,
        AudioManager audio, RadioManager radios, RemovableDriveManager drives,
        KeepAwakeService? keepAwake = null, bool previewOnly = false, SdFormatManager? formats = null,
        MessageWindow? activationWindow = null)
        : this(config, store, steamInput, monitor, modes, keepAwake, previewOnly,
            new OverlaySources(DisplayModes: DisplayModeAccess.Unavailable),
            audio, radios: radios, drives: drives, formats: formats, activationWindow: activationWindow)
    {
    }

    internal OverlayController(AppConfig config, ConfigStore store, SteamInputBlocker steamInput,
        SteamMonitor? monitor, SessionModes modes,
        KeepAwakeService? keepAwake, bool previewOnly, OverlaySources? sources,
        AudioManager? audio = null,
        AudioProfileService? audioProfiles = null,
        RadioManager? radios = null,
        DevicePowerPresets? powerPresets = null,
        DevicePowerAssignments? powerAssignments = null,
        RemovableDriveManager? drives = null,
        SdFormatManager? formats = null,
        DisplayTimeouts? displayTimeouts = null, MessageWindow? activationWindow = null,
        WindowsPowerPolicy? power = null, PowerButtonSetting? powerButton = null)
    {
        // A preview writes nothing, so a set of its own never needs the session's lock.
        power ??= WindowsPowerPolicy.OverWindows();
        _services = new OverlaySheetServices(sources ?? new OverlaySources(DisplayModes: DisplayModeAccess.Unavailable),
            power, powerPresets, powerAssignments, previewOnly);
        _store = store;
        // Lease owner names are per controller instance on purpose: a replacement controller (the Settings
        // preview's "Test panel" pressed twice) claims under its own names, so the outgoing controller's
        // release cannot drop the live surface's lease.
        _sheet = new OverlaySurfaceSlot(QuickAccessSurface, SteamInputBlocker.NewOwner("overlay-controller"));
        _bar = new OverlaySurfaceSlot(TaskbarSurface, SteamInputBlocker.NewOwner("overlay-taskbar"));
        _steamInput = steamInput ?? throw new ArgumentNullException(nameof(steamInput));
        _powerState = new OverlayPowerState(power, keepAwake, displayTimeouts, previewOnly, powerButton);
        _sessionAudio = audio ?? throw new ArgumentNullException(nameof(audio));
        _audioProfiles = audioProfiles;
        _sessionRadios = radios ?? throw new ArgumentNullException(nameof(radios));
        _sessionDrives = drives ?? throw new ArgumentNullException(nameof(drives));
        if (formats is not null)
        {
            _formatManager = formats;
            _formatManager.Progress.Finished += OnFormatFinished;
        }

        _config = config;
        Pins = new QuickAccessPinStore(config, store, previewOnly);
        _monitor = monitor;
        _modes = modes;
        _hasKeepAwake = keepAwake is not null;
        _previewOnly = previewOnly;
        // While the sheet shows, the strip marks the window it opened over rather than itself.
        _openApps = new OverlayOpenApps(
            () => SurfaceShowing() ? _restoreFocusTo : NativeMethods.GetForegroundWindow(),
            SurfaceShowing, () => _disposed);

        _modes.SteamStartFailed += WarnOrReopen;
        _modes.SteamExitShowOverlayRequested += ShowOverlay;
        _steamInput.RecoveryWarningRaised += OnSteamInputRecoveryWarning;

        _activation = new OverlayActivation(config, activationWindow);
        _activation.QuickAccessRequested += ShowOverlay;
        _activation.TaskbarRequested += ShowTaskbar;
    }

    internal Func<CancellationToken, Task<bool>>? ShowOnScreenKeyboard { get; set; }

    /// <summary>
    ///     Opens the session's one Settings window, or brings it back, and returns it; null on a preview
    ///     surface, whose sheet then has no Settings row.
    /// </summary>
    internal Func<Task<Window>>? OpenSettings { get; set; }

    /// <summary>
    ///     Performs a confirmed machine power action. Only the session sets it; without it, and on every
    ///     preview surface, the sheet's power rows dismiss without acting on the machine.
    /// </summary>
    internal Action<SessionPowerAction>? RequestPowerAction { get; set; }

    /// <summary>
    ///     The session's one path that brings a game window forward, used by Open apps picks and the close
    ///     refocus; null on the overlay test and preview surfaces, which then restore no focus on close.
    /// </summary>
    internal GameWindowReturn? GameReturn { get; set; }

    /// <summary>
    ///     The session's Steam client the sheet's Steam rows read and write through; null on a surface
    ///     without one (the Settings preview), whose Steam rows then report Steam as unreachable.
    /// </summary>
    internal SteamClient? SteamClient { get; set; }

    /// <summary>The Steam client build notice the sheet shows above every destination, or null.</summary>
    /// <remarks>Set once by the session before the sheet first opens.</remarks>
    internal SteamClientBuildNotice? BuildNotice
    {
        get;
        set
        {
            field?.Changed -= OnBuildNoticeChanged;
            field = value;
            field?.Changed += OnBuildNoticeChanged;
        }
    }

    /// <summary>
    ///     The shared removable-storage format manager backing the Tools
    ///     tab's Format SD Card / Add Steam Library flow. Created on first use and
    ///     kept for the controller's lifetime so a format survives the overlay
    ///     closing; a completion reached while the overlay is closed surfaces
    ///     through the warning bar on the next open.
    /// </summary>
    private SdFormatManager FormatManager => _formatManager
                                             ?? throw new InvalidOperationException(
                                                 "The session must supply its format manager.");

    /// <summary>Whether the power menu currently consumes short power-button requests.</summary>
    public bool PowerMenuOpen => Sheet?.Surfaces.IsPowerMenuOpen == true;

    /// <summary>The pin list this controller shows and saves.</summary>
    internal QuickAccessPinStore Pins { get; }

    /// <summary>Releases overlay windows, input activation, and lifecycle subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keyboardRequestCancellation?.Cancel();
        _windowReturnCancellation?.Cancel();
        _focusRestoreCancellation?.Cancel();
        Sheet?.ReleaseProgramsPresentation();
        DisposeNotes();
        Sheet?.Surfaces.CloseAll();
        DisposeTaskbar();
        // Deliberately NOT retracting the injected Steam UI (tabs, badge, Wi-Fi AP)
        // here: both the Settings preview and session shutdown call this Dispose, and
        // retracting from the preview would tear the LIVE session's tabs out of Big
        // Picture. ShellSession owns that teardown.
        AttachTrayHost(null);
        _modes.SteamStartFailed -= WarnOrReopen;
        _modes.SteamExitShowOverlayRequested -= ShowOverlay;
        _steamInput.RecoveryWarningRaised -= OnSteamInputRecoveryWarning;
        // The Keep awake and display timeout owners belong to ShellSession; only the subscriptions are ours.
        _powerState.Dispose();
        if (_formatManager is not null)
        {
            // The composition owns the format manager; only the subscription is ours.
            _formatManager.Progress.Finished -= OnFormatFinished;
        }

        _activation.QuickAccessRequested -= ShowOverlay;
        _activation.TaskbarRequested -= ShowTaskbar;
        // The hotkey and the chord, the controller polling they read, then the edge recognizer.
        _activation.Dispose();
        // Frees the strip's icons now: a surface that closes after this skips its post-close work.
        _openApps.Close();
        if (Sheet is not null)
        {
            // This controller owes a lease release and a UI capture release (its
            // overlay is open / pending close). End both NOW, not in the deferred
            // Closed handler 150 ms from here: a replacement controller (Test panel
            // pressed again) may acquire a lease in between, and at shutdown the
            // dispatcher may stop before the timer fires. Both releases are
            // idempotent, so the Closed handler's second call only logs.
            ReleaseSteamInputLease();
            ReleaseUiSurface(QuickAccessSurface);
        }

        // Close through the same deferred path as every dismissal: an immediate
        // Close() would skip the 150 ms grace and bring back the ghost clicks the
        // deferral exists for. When Dispose runs during process exit the
        // dispatcher may stop pumping before the 150 ms lands and the Close()
        // never runs — deliberately fine: the lease and the capture claim were
        // released synchronously above, and process exit destroys the window anyway.
        // The sheet's deferred Closed handler clears the icon cache; disposing it
        // here would leave the still-open window rendering disposed bitmaps for
        // the 150 ms grace.
        CloseOverlay();
    }

    // The notice changes on a worker; the sheet's view model changes on the UI thread.
    private void OnBuildNoticeChanged()
    {
        Dispatcher.UIThread.Post(() => _overlayViewModel?.SteamBuildNoticeText = BuildNotice?.Text ?? "");
    }

    /// <summary>Sets a non-fatal warning to show the next time the overlay opens.</summary>
    /// <param name="warning">The user-facing warning text, or an empty string to clear it.</param>
    public void SetWarning(string warning)
    {
        _pendingWarning = warning;
        _overlayViewModel?.WarningText = warning;
    }

    private void OnFormatFinished(string message, bool success)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // While the overlay is open the sub-view already shows the outcome.
            // If it was closed mid-format (the run outlives the window), reopen
            // it to surface the result through the warning bar.
            if (Sheet is not null || _disposed)
            {
                return;
            }

            SetWarning(message);
            ShowOverlay();
        });
    }

    /// <summary>Routes Back/B through dialog, nested-page, destination-root, then close priority.</summary>
    private void OnOverlayBack()
    {
        if (Sheet?.TryCancelSubView() == true)
        {
            return;
        }

        CloseOverlay();
    }

    /// <summary>Applies a freshly loaded config (settings saved in another process).</summary>
    /// <param name="config">
    ///     The freshly loaded configuration; it replaces the previous
    ///     instance wholesale, so runtime state must stay on the controllers rather than
    ///     on the configuration object.
    /// </param>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Pins.Reload(config);
        Sheet?.Placement.SetBlurRadius(config.OverlayBlurRadius);
        _services.Sources.CommonPlugins?.ApplyPins(config.PluginWidgetPins);
        // The master CEF switch is owned by ShellSession, which retracts injected UI
        // before closing it — setting it here as well would cut that retraction off.
        // UI-thread only: this writes view-model state, control titles and the
        // gamepad's DispatcherTimer with no marshalling of its own. ShellSession's
        // debounced config watcher posts it after applying the session-wide accent
        // and session modes itself.
        _activation.ApplyConfig(config, Sheet is not null || Taskbar is not null);
        if (_overlayViewModel is not null)
        {
            // Keep the open panel's footer glyphs in step with the (already live)
            // Nintendo A/B input mapping.
            _overlayViewModel.GlyphStyle = config.GlyphStyle;
            // A feature the user just turned off must lose its button now, not at the
            // next reopen: pressing one would drive an integration that is already
            // disabled and answer with an unreachable-Steam warning.
            _services.ApplyVisibility(_overlayViewModel, config);
            Sheet?.PanelSteamLaunch.RefreshLabels();
            Sheet?.SetPins(Pins.Ids);
        }

        if (Sheet is not null && _overlayRequiresSteamLease)
        {
            AcquireSteamInputLease();
        }

        if (Taskbar is not null)
        {
            AcquireSteamInputLease(_bar.LeaseOwner);
        }
    }

    private void OnSteamInputRecoveryWarning(string warning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                SetWarning(warning);
            }
        });
    }

    /// <summary>
    ///     Raised whenever quick access comes up (hotkey, swipe, chord,
    ///     Steam-exit pop, warning reopen). The boot splash dismisses on it — the
    ///     panel always outranks the splash.
    /// </summary>
    public event Action? OverlayShown;

    /// <summary>Raised before a focus-taking WSGM surface starts consuming controller input.</summary>
    internal event Action<string>? UiSurfaceOpened;

    /// <summary>Raised after a focus-taking WSGM surface stops consuming controller input.</summary>
    internal event Action<string>? UiSurfaceClosed;

    /// <summary>Shows and activates the overlay unless it has already been disposed.</summary>
    public void ShowOverlay()
    {
        _powerMenuOnly = false;
        ShowOverlayCore(true);
    }

    private void ShowOverlayCore(bool acquireSteamLease)
    {
        try
        {
            OpenOverlayCore(acquireSteamLease);
        }
        catch
        {
            // Construction, source attachment and input startup can fail before Show. All of them
            // already own the lease, so they need the same teardown as a failed native window show.
            FailSurfaceOpen(_sheet, OnOverlayClosed);
            throw;
        }
    }

    private void OpenOverlayCore(bool acquireSteamLease)
    {
        if (_disposed)
        {
            return;
        }

        // The sheet replaces an open taskbar and returns to the window the taskbar opened over.
        var handedOver = HandTaskbarToOverlay();
        _overlayRequiresSteamLease |= acquireSteamLease;
        PrepareSurfaceOpen(Sheet is null, handedOver, acquireSteamLease ? _sheet.LeaseOwner : null);
        if (Sheet is not null)
        {
            ReactivateOpenOverlay(Sheet);
            return;
        }

        var openStarted = Stopwatch.GetTimestamp();
        // One process-table scan per open: the view model and the UI scale both need it.
        var explorerRunning = ExplorerControl.IsDesktopShellRunning();
        var vm = new OverlayViewModel
        {
            HomeAppName = "Steam",
            GlyphStyle = _config.GlyphStyle,
            ShowKeepAwake = _hasKeepAwake,
            ModeSwitchAvailable = !_previewOnly,
            SettingsAvailable = OpenSettings is not null,
            PowerTimeoutsEditable = !_previewOnly
        };
        _services.ApplyVisibility(vm, _config);
        RefreshSessionState(vm, explorerRunning);

        _overlayViewModel = vm;
        // The Open apps strip, tray and status pills live only while the sheet is
        // open; the Closed handler below disposes them with the window.
        var switcher = _openApps.Open();
        // Shares the session's audio and radio managers when there are any, so the sheet's
        // pills and Steam's own surfaces are the same state rather than two views that can disagree.
        _systemStatus = new SystemStatus(_sessionAudio, _sessionRadios, _sessionDrives);
        _systemStatus.Start();
        var setupDone = Stopwatch.GetTimestamp();
        var overlay = new OverlayWindow(_store, vm, switcher, _systemStatus, _windowSession,
            static window => window.Placement.Dock(), UiScale(explorerRunning),
            WindowCenter(_restoreFocusTo), SteamClient);
        _sheet.Attach(overlay);
        overlay.Placement.SetBlurRadius(_config.OverlayBlurRadius);
        var powerSchemes = _services.AttachPower(overlay);
        overlay.OnScreenKeyboardRequested += async () => await RequestOnScreenKeyboardAsync();
        _powerState.Attach(overlay, vm);
        _powerState.RefreshWindowsPolicies(overlay);
        _powerState.FollowSchemes(powerSchemes, vm);
        var constructDone = Stopwatch.GetTimestamp();
        overlay.AttachSources(_services.Sources);
        AttachNotes(overlay);
        overlay.ProgramsHost.ActivateRequested += ActivateProgram;
        overlay.HotkeysHost.SendRequested += SendHotkey;
        overlay.SetPins(Pins.Ids);
        overlay.PinToggleRequested += OnPinToggleRequested;
        overlay.WindowPicked += PickWindow;
        overlay.TrayIconActivated += OnTrayIconActivated;
        overlay.RadioPanelRequested += ShowRadioPanel;
        overlay.AudioPanelRequested += ShowAudioPanel;
        overlay.EjectPanelRequested += ShowEjectPanel;
        Log.Debug("Quick access shown (Open apps snapshot queued).");
        WireOverlayRequests(overlay, vm);
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(Sheet, overlay))
            {
                OnOverlayClosed();
            }
        };

        overlay.StorageFormatHost.Attach(FormatManager);
        overlay.PowerMenuRequested += TogglePowerMenu;
        overlay.Surfaces.Closed += () =>
        {
            if (_powerMenuOnly && Sheet is { Surfaces.HasActive: false })
            {
                CloseOverlay();
            }
        };

        // Internal text entry shares this window and its single navigation owner.
        ShowSurface(_sheet, OverlayInput.Create(overlay, _activation.Gamepad, OnOverlayBack, IsNintendoLayout));

        var showDone = Stopwatch.GetTimestamp();
        LogOpenTimings(openStarted, setupDone, constructDone, showDone);
        _powerState.StartWakeLockRefresh();
        // In Game Mode the taskbar owns the open windows and the sheet hides its strip, so it is read once only.
        if (explorerRunning)
        {
            _openApps.Start();
        }
    }

    /// <summary>Brings the open sheet back to the front, cancelling a deferred close in progress.</summary>
    /// <param name="overlay">The sheet that is still open.</param>
    private void ReactivateOpenOverlay(OverlayWindow overlay)
    {
        ResumeSurface(_sheet);
        if (_overlayViewModel is not null)
        {
            // Steam may have died or the desktop may have changed while the panel stayed open.
            RefreshSessionState(_overlayViewModel, ExplorerControl.IsDesktopShellRunning());
            _powerState.StartWakeLockRefresh();
        }

        _openApps.Refresh();
        overlay.ResetForResummon();
        overlay.Activate();
    }

    /// <summary>Copies the session state an open or re-summoned sheet shows into its view model.</summary>
    /// <param name="vm">The sheet's view model.</param>
    /// <param name="explorerRunning">Whether the desktop shell runs, read once by the caller.</param>
    private void RefreshSessionState(OverlayViewModel vm, bool explorerRunning)
    {
        vm.WarningText = _pendingWarning;
        vm.SteamBuildNoticeText = BuildNotice?.Text ?? "";
        vm.ExplorerRunning = explorerRunning;
        vm.HomeAppAlive = _monitor?.IsAlive ?? false;
        _powerState.Refresh(vm);
    }

    /// <summary>Releases everything the sheet held once its window has closed.</summary>
    private void OnOverlayClosed()
    {
        // Detach surfaces before disposing the live managers they observe.
        Sheet?.Surfaces.CloseAll();
        _systemStatus?.Dispose();
        _systemStatus = null;
        _powerState.Detach();
        _powerMenuOnly = false;
        _overlayRequiresSteamLease = false;
        _overlayViewModel = null;
        var reopenForWarning = _reopenOverlayForWarning;
        _reopenOverlayForWarning = false;
        // Gives Steam its pad back the moment the sheet is gone. This ends only the sheet's own claim: a
        // Settings window opened from it claimed the lease in Opened, before this deferred close, and keeps it.
        if (!EndSurface(_sheet))
        {
            return;
        }

        if (reopenForWarning)
        {
            Dispatcher.UIThread.Post(ShowOverlay);
        }
    }

    private static void LogOpenTimings(long openStarted, long setupDone, long constructDone, long showDone)
    {
        Dispatcher.UIThread.Post(
            () => Log.Info(
                "Quick access open timings: setup "
                + $"{Stopwatch.GetElapsedTime(openStarted, setupDone).TotalMilliseconds:F0} ms, "
                + $"construct {Stopwatch.GetElapsedTime(setupDone, constructDone).TotalMilliseconds:F0} ms, "
                + $"show {Stopwatch.GetElapsedTime(constructDone, showDone).TotalMilliseconds:F0} ms, "
                + $"first frame {Stopwatch.GetElapsedTime(showDone).TotalMilliseconds:F0} ms."),
            DispatcherPriority.Background);
    }

    private static PixelPoint? WindowCenter(nint window)
    {
        if (window == 0 || !NativeMethods.GetWindowRect(window, out var rect)
                        || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return null;
        }

        return new PixelPoint(
            rect.Left + (rect.Right - rect.Left) / 2,
            rect.Top + (rect.Bottom - rect.Top) / 2);
    }

    /// <summary>
    ///     Pins or unpins a row on the Quick access root: the controller's own list keeps
    ///     the sheet consistent immediately; the file write happens off-thread, chained
    ///     behind the previous one so saves land in press order, and the config watcher's
    ///     reload then hands back the same list. A preview surface (Settings' Test sheet)
    ///     never writes.
    /// </summary>
    /// <param name="id">Stable section or capability pin ID; a toggle is saved in sequence with earlier toggles.</param>
    internal void OnPinToggleRequested(string id)
    {
        var pins = Pins.Toggle(id);
        Sheet?.SetPins(pins);
        Pins.Save();
    }

    /// <summary>
    ///     Constructs and discards one throwaway sheet so the first real swipe does not pay
    ///     the process's one-time cost for its largest window — compiled-XAML populate JIT and the
    ///     instantiation of every page host — measured as a ~1.5 s first open on the Claw while every
    ///     later open is immediate.
    /// </summary>
    /// <remarks>
    ///     UI thread, intended for an idle moment after the session is up. The throwaway
    ///     window is never shown: the constructor is wiring only, and nothing live is attached — no
    ///     status Start, no device bridge, no performance source, no lease.
    /// </remarks>
    public void WarmUp()
    {
        if (_disposed || Sheet is not null)
        {
            return;
        }

        try
        {
            var status = new SystemStatus(_sessionAudio, _sessionRadios, _sessionDrives);
            var window = new OverlayWindow(
                _store,
                new OverlayViewModel(),
                new AppSwitcherViewModel(),
                status,
                _windowSession,
                static window => window.Placement.Dock(),
                UiScale(ExplorerControl.IsDesktopShellRunning()))
            {
                WarmingUp = true,
                // Far off-screen: the render backend (Skia GPU context, ANGLE device, the
                // process-global glyph/geometry caches) initializes on the first PRESENTED window
                // and survives its close — proven by the second open being a fresh window that is
                // already fast. Priming it here moves that ~0.5 s off the user's first swipe. The
                // topmost sheet is parked at a negative origin so the warm frame never flashes.
                Position = new PixelPoint(-32000, -32000),
                ShowInTaskbar = false
            };
            window.Show();
            // Let one full render pass complete before tearing the warm window down, then free
            // it — the primed backend and caches are process-global and outlive it.
            Dispatcher.UIThread.Post(
                () =>
                {
                    window.Close();
                    status.Dispose();
                    Log.Info("Quick access sheet warmed for first open.");
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            // Warm-up is an optimization; the first swipe still works without it.
            Log.Warn($"Quick access warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Opens or closes the primary overlay exactly once.</summary>
    public void ToggleOverlay()
    {
        if (Sheet is null)
        {
            ShowOverlay();
        }
        else
        {
            CloseOverlay();
        }
    }

    /// <summary>Opens the overlay at the device destination when available.</summary>
    /// <remarks>The provisional device destination is selected by the surface as it is composed.</remarks>
    public void ShowDevicePage()
    {
        ShowOverlay();
        Sheet?.SelectDeviceDestination();
    }

    internal bool ShowBluetoothPanel()
    {
        if (_disposed)
        {
            return false;
        }

        ShowOverlay();
        ShowRadioPanel(true);
        return Sheet?.Surfaces.HasActive == true;
    }

    private void ShowRadioPanel(bool bluetooth)
    {
        if (_systemStatus is not null)
        {
            Sheet?.Surfaces.ShowRadio(new RadioPanel(_systemStatus.Radios, bluetooth));
        }
    }

    private void ShowAudioPanel()
    {
        if (_systemStatus is not null)
        {
            Sheet?.Surfaces.ShowAudio(new AudioPanel(_systemStatus.Audio, _audioProfiles));
        }
    }

    private void ShowEjectPanel()
    {
        if (_systemStatus is not null)
        {
            Sheet?.Surfaces.ShowEject(new EjectPanel(_systemStatus.Drives));
        }
    }

    /// <summary>
    ///     The desktop-DPI factor for WSGM surfaces. The boost exists ONLY
    ///     to compensate game mode's forced 100% display scaling — in desktop mode
    ///     the display already runs at the user's real scaling and Avalonia applies
    ///     it, so boosting again would double up (device-reported: surfaces rendered
    ///     huge on a 100% desktop when the recommended-scale fallback fired there).
    /// </summary>
    private double UiScale(bool explorerRunning)
    {
        return explorerRunning
            ? 1.0
            : DisplayScale.GetUiScalePercent(_config) / 100.0;
    }

    private bool IsNintendoLayout()
    {
        return _config.GlyphStyle == GlyphStyle.Nintendo;
    }

    private void CloseOverlay()
    {
        CloseOverlay(false);
    }

    private void CloseOverlay(bool preserveWindowReturn)
    {
        if (!preserveWindowReturn)
        {
            _windowReturnCancellation?.Cancel();
        }

        _pendingWarning = "";
        _reopenOverlayForWarning = false;
        // Deferred: a touch tap's DefWindowProc promotion delivers a synthesized mouse click AFTER this
        // dispatch. If the window were already destroyed, that click would land on whatever sits underneath
        // (user-reproduced). Kept open a beat, the window's own hook eats it. A re-summon in time cancels it.
        _sheet.BeginClose();
    }

    /// <summary>UI sink for the coordinator's Steam start failures.</summary>
    private void WarnOrReopen(string warning)
    {
        SetWarning(warning);
        // The request normally closes the overlay first. If that close is
        // asynchronous, its Closed handler will recreate it with the error.
        if (Sheet is null)
        {
            ShowOverlay();
        }
        else
        {
            _reopenOverlayForWarning = true;
        }
    }
}
