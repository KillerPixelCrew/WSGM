using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Owns the fullscreen overlay, activation inputs, gamepad navigation and paired capture/lease transitions.
/// </summary>
/// <remarks>
///     Construct, open and dispose on the Avalonia UI thread. Session services are borrowed; window-local
///     subscriptions and input claims are released on close or disposal. Native lease work is asynchronous.
/// </remarks>
public sealed partial class OverlayController : IDisposable
{
    private const string QuickAccessSurface = "quick-access";
    private const string SettingsSurface = "settings";
    private readonly bool _activationEnabled;
    private readonly AudioProfileService? _audioProfiles;
    private readonly GamepadChordWatcher? _chordWatcher;

    /// <summary>
    ///     The session's display-off timeouts, shared with Steam's Screensaver settings, or null when the
    ///     controller has no session behind it.
    /// </summary>
    private readonly DisplayTimeouts? _displayTimeouts;


    private readonly SdFormatManager? _formatManager;

    private readonly GamepadService _gamepad = new();
    private readonly HotkeyService? _hotkey;
    private readonly KeepAwakeService? _keepAwake;

    /// <summary>
    ///     This controller's identity in the blocker's ownership
    ///     set. Per instance on purpose: a replacement controller (the Settings preview's
    ///     "Test panel" pressed twice) claims the lease under its own name, so the
    ///     outgoing controller's release cannot drop the live surface's lease.
    /// </summary>
    private readonly string _leaseOwner = SteamInputBlocker.NewOwner("overlay-controller");

    private readonly SessionModes _modes;
    private readonly SteamMonitor? _monitor;

    /// <summary>
    ///     The Windows power policy owners the sheet's scheme, core and timeout rows use: the session's,
    ///     so their writes share its scheme lock, or a set of this controller's own for a preview.
    /// </summary>
    private readonly WindowsPowerPolicy _power;

    private readonly DevicePowerAssignments? _powerAssignments;
    private readonly DevicePowerPresets? _powerPresets;
    private readonly NativeQamPowerProfileService _powerProfiles;
    private readonly bool _previewOnly;

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

    private readonly OverlaySources _sources;

    /// <summary>The process's Steam Input lease owner this controller claims through.</summary>
    private readonly SteamInputBlocker _steamInput;

    private readonly ConfigStore _store;

    /// <summary>Names of UI surfaces currently holding this controller's shared input claim.</summary>
    private readonly HashSet<string> _uiSurfaces = new(StringComparer.Ordinal);

    private readonly OverlayWindow.SessionState _windowSession = new();


    private AppConfig _config;
    private bool _dialogPriorNavigation;


    private bool _disposed;

    private WindowIconCache? _iconCache;
    private CancellationTokenSource? _keyboardRequestCancellation;
    private bool _keyboardRequestPending;

    private string? _lastWakeLockError;

    // The last claim end, which window activation waits for so the game gets its controller back first.
    private Task _leaseRelease = Task.CompletedTask;
    private GamepadNavigation? _navigation;
    private OverlayWindow? _overlay;
    private bool _overlayRequiresSteamLease;
    private OverlayViewModel? _overlayViewModel;
    private IDisposable? _pendingClose;
    private string _pendingWarning = "";

    // The pins this controller shows. Seeded from config and replaced on reload; a toggle never writes the
    // shared config object, and its save is chained behind the previous one so the file ends in press order.
    private List<string> _pins;
    private bool _powerMenuOnly;
    private Task _powerTimeoutWrite = Task.CompletedTask;

    private bool _reopenOverlayForWarning;

    /// <summary>
    ///     Window focused when the overlay opened. Exclusive-fullscreen games
    ///     minimize the moment our panel takes focus — closing the panel calls them
    ///     back (restore + foreground), unless an overlay action redirected focus.
    /// </summary>
    private nint _restoreFocusTo;

    private HashSet<uint> _steamPids = [];
    private DateTime _steamPidsAtUtc;
    private bool _suppressFocusRestore;
    private DispatcherTimer? _switcherRefresh;
    private int _switcherRefreshInFlight;
    private AppSwitcherViewModel? _switcherViewModel;
    private SystemStatus? _systemStatus;
    private TouchSwipeMonitor? _touchSwipes;
    private TrayHost? _trayHost;

    private bool _wakeLockQueryRunning;

    private DispatcherTimer? _wakeLockRefresh;
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
        WindowsPowerPolicy? power = null)
    {
        _sources = sources ?? new OverlaySources(DisplayModes: DisplayModeAccess.Unavailable);
        // A preview writes nothing, so a set of its own never needs the session's lock.
        _power = power ?? WindowsPowerPolicy.OverWindows();
        _powerProfiles = _sources.PowerProfiles ?? new NativeQamPowerProfileService(_power.Schemes,
            _ => throw new InvalidOperationException("A preview cannot persist a power-profile selection."));
        _store = store;
        _steamInput = steamInput ?? throw new ArgumentNullException(nameof(steamInput));
        _displayTimeouts = displayTimeouts;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed += OnDisplayTimeoutsChanged;
        }

        _powerPresets = powerPresets;
        _powerAssignments = powerAssignments;
        _sessionAudio = audio ?? throw new ArgumentNullException(nameof(audio));
        _audioProfiles = audioProfiles;
        _sessionRadios = radios ?? throw new ArgumentNullException(nameof(radios));
        _sessionDrives = drives ?? throw new ArgumentNullException(nameof(drives));
        if (formats is not null)
        {
            _formatManager = formats;
            _formatManager.Finished += OnFormatFinished;
        }

        _config = config;
        _pins = [.. config.QuickAccessPins];
        _monitor = monitor;
        _modes = modes;
        _keepAwake = keepAwake;
        _previewOnly = previewOnly;
        _activationEnabled = activationWindow is not null;
        if (_keepAwake is not null)
        {
            _keepAwake.StateChanged += OnKeepAwakeStateChanged;
        }

        _modes.SteamStartFailed += WarnOrReopen;
        _modes.SteamExitShowOverlayRequested += ShowOverlay;
        _steamInput.RecoveryWarningRaised += OnSteamInputRecoveryWarning;

        if (activationWindow is not null)
        {
            _hotkey = new HotkeyService(activationWindow);
            _hotkey.Pressed += ShowOverlay;
            _hotkey.Apply(config.Hotkey);
            _chordWatcher = new GamepadChordWatcher(_gamepad, config.GamepadChord);
            _chordWatcher.Triggered += ShowOverlay;
            if (config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0)
            {
                _gamepad.Start();
            }
        }

        ApplyGestures(config.Gestures);
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

    internal GameWindowReturn? GameReturn { get; set; }

    /// <summary>
    ///     The session's Steam client the sheet's Steam rows read and write through; null on a surface
    ///     without one (the Settings preview), whose Steam rows then report Steam as unreachable.
    /// </summary>
    internal SteamClient? SteamClient { get; set; }


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
    public bool PowerMenuOpen => _overlay?.IsPowerMenuOpen == true;

    /// <summary>Completes after the pin changes submitted so far have finished saving.</summary>
    internal Task PinWrites { get; private set; } = Task.CompletedTask;

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
        CloseKeyboardNow();
        // Deliberately NOT retracting the injected Steam UI (tabs, badge, Wi-Fi AP)
        // here: both the Settings preview and session shutdown call this Dispose, and
        // retracting from the preview would tear the LIVE session's tabs out of Big
        // Picture. ShellSession owns that teardown.
        AttachTrayHost(null);
        _modes.SteamStartFailed -= WarnOrReopen;
        _modes.SteamExitShowOverlayRequested -= ShowOverlay;
        _steamInput.RecoveryWarningRaised -= OnSteamInputRecoveryWarning;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed -= OnDisplayTimeoutsChanged;
        }

        if (_keepAwake is not null)
        {
            // The service belongs to ShellSession; only the subscription is ours.
            _keepAwake.StateChanged -= OnKeepAwakeStateChanged;
        }

        if (_formatManager is not null)
        {
            // The composition owns the format manager; only the subscription is ours.
            _formatManager.Finished -= OnFormatFinished;
        }

        if (_hotkey is not null)
        {
            _hotkey.Pressed -= ShowOverlay;
            _hotkey.Dispose();
        }

        if (_chordWatcher is not null)
        {
            _chordWatcher.Triggered -= ShowOverlay;
            _chordWatcher.Dispose();
        }

        // Before the service it subscribes to, so the unsubscribe lands on a live object.
        _gamepad.Dispose();
        DisposeTouchEdges();
        StopSwitcherRefresh();
        if (_overlay is not null)
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
            if (_overlay is not null || _disposed)
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
        if (_overlay?.TryCancelSubView() == true)
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
        // A reload can echo an earlier save while a later toggle is still waiting for the store.
        // Keep that pending intent as the next toggle's baseline until the write chain has settled.
        if (PinWrites.IsCompleted)
        {
            _pins = [.. config.QuickAccessPins];
        }

        _overlay?.SetBlurRadius(config.OverlayBlurRadius);
        _sources.CommonPlugins?.ApplyPins(config.PluginWidgetPins);
        // The master CEF switch is owned by ShellSession, which retracts injected UI
        // before closing it — setting it here as well would cut that retraction off.
        // UI-thread only: this writes view-model state, control titles and the
        // gamepad's DispatcherTimer with no marshalling of its own. ShellSession's
        // debounced config watcher posts it after applying the session-wide accent
        // and session modes itself.
        _hotkey?.Apply(config.Hotkey);
        _chordWatcher?.ApplyConfig(config.GamepadChord);
        var chordActive = _activationEnabled && config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0;
        switch (chordActive)
        {
            case true when !_gamepad.IsRunning:
                _gamepad.Start();
                break;
            case false when _overlay is null && _gamepad.IsRunning:
                _gamepad.Stop();
                break;
        }

        ApplyGestures(config.Gestures);
        if (_overlayViewModel is not null)
        {
            // Keep the open panel's footer glyphs in step with the (already live)
            // Nintendo A/B input mapping.
            _overlayViewModel.GlyphStyle = config.GlyphStyle;
            // A feature the user just turned off must lose its button now, not at the
            // next reopen: pressing one would drive an integration that is already
            // disabled and answer with an unreachable-Steam warning.
            ApplyCefVisibility(_overlayViewModel, config);
            _overlay?.RefreshLaunchFixLabels();
            _overlay?.SetPins(_pins);
        }

        if (_overlay is not null && _overlayRequiresSteamLease)
        {
            AcquireSteamInputLease();
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
    ///     Hides each CEF feature's sidebar button when that feature is off, so
    ///     a disabled integration has no entry point. With CEF off entirely the
    ///     launch-wrapper buttons fall back to copying the command instead of
    ///     disappearing, which keeps them useful.
    /// </summary>
    /// <param name="vm">The panel's view model.</param>
    /// <param name="config">The configuration to read the gates from.</param>
    private void ApplyCefVisibility(OverlayViewModel vm, AppConfig config)
    {
        vm.ShowGameLibrary = config.Cef.Enabled && _sources.GameLibrary is not null;
        vm.ShowArtwork = config.Cef.Enabled && _sources.Artwork is not null;
        vm.ShowThemes = config.Cef.Enabled && _sources.Themes is not null;
        vm.ShowAnimations = config.Cef.Enabled && _sources.Animations is not null;
        vm.ShowSounds = _sources.Sounds is not null;
        vm.ShowLibraryTabs = config.Cef is { Enabled: true, LibraryTabs: true };
        vm.ShowCardManager = config.Cef is { Enabled: true, CardManager: true };
        vm.ShowSdCard = config.Cef is { Enabled: true, SdFormat: true };
        vm.ConfigureLaunchOptionsLive = config.Cef.Enabled;
        vm.InputLeaseUsesShim = config.SteamInputManagementEnabled;
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
            var failed = _overlay;
            OnOverlayClosed();
            failed?.Close();
            throw;
        }
    }

    private void OpenOverlayCore(bool acquireSteamLease)
    {
        if (_disposed)
        {
            return;
        }

        _windowReturnCancellation?.Cancel();
        _keyboardRequestCancellation?.Cancel();
        OverlayShown?.Invoke();
        if (_overlay is null)
        {
            _restoreFocusTo = NativeMethods.GetForegroundWindow();
            _suppressFocusRestore = false;
        }

        _overlayRequiresSteamLease |= acquireSteamLease;
        if (acquireSteamLease)
        {
            AcquireSteamInputLease();
        }

        HideTouchEdges();
        if (_overlay is not null)
        {
            ReactivateOpenOverlay(_overlay);
            return;
        }

        var openStarted = Stopwatch.GetTimestamp();
        // One process-table scan per open: the view model and the UI scale both need it.
        var explorerRunning = ExplorerControl.IsDesktopShellRunning();
        var vm = new OverlayViewModel
        {
            ExplorerRunning = explorerRunning,
            HomeAppAlive = _monitor?.IsAlive ?? false,
            HomeAppName = "Steam",
            GlyphStyle = _config.GlyphStyle,
            WarningText = _pendingWarning,
            ShowKeepAwake = _keepAwake is not null,
            ModeSwitchAvailable = !_previewOnly,
            SettingsAvailable = OpenSettings is not null,
            PowerTimeoutsEditable = !_previewOnly,
            KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off,
            KeepAwakeDownloadActive = _keepAwake?.DownloadHold ?? false
        };
        ApplyCefVisibility(vm, _config);
        RefreshPowerTimeouts(vm);

        _overlayViewModel = vm;
        // The Open apps strip, tray and status pills live only while the sheet is
        // open; the Closed handler below disposes them with the window.
        // 48 px rasters downscale crisply into the chips' 18-DIP icons on high-DPI panels.
        _iconCache ??= new WindowIconCache(48);
        var switcher = new AppSwitcherViewModel();
        _switcherViewModel = switcher;
        RefreshSwitcherEntries();
        OnTrayIconsChanged();
        // Shares the session's audio and radio managers when there are any, so the sheet's
        // pills and Steam's own surfaces are the same state rather than two views that can disagree.
        _systemStatus = new SystemStatus(_sessionAudio, _sessionRadios, _sessionDrives);
        _systemStatus.Start();
        var setupDone = Stopwatch.GetTimestamp();
        _overlay = new OverlayWindow(_store, vm, switcher, _systemStatus, _windowSession,
            static window => window.DockToTopEdge(), UiScale(explorerRunning),
            WindowCenter(_restoreFocusTo), SteamClient);
        _overlay.SetBlurRadius(_config.OverlayBlurRadius);
        if (_sources.Brightness is { } brightness)
        {
            _overlay.AttachBrightness(brightness,
                _previewOnly ? DisplayModeAccess.Unavailable : _sources.DisplayModes ?? DisplayModeAccess.Unavailable);
        }

        if (_sources.ManualTdp is { } manual)
        {
            _overlay.AttachManualTdp(manual);
        }

        _overlay.OnScreenKeyboardRequested += async () => await RequestOnScreenKeyboardAsync();
        var powerSchemes = new PowerSchemeSelection(_powerProfiles,
            _previewOnly || _sources.PowerProfiles is null);
        _overlay.AttachPowerSchemes(powerSchemes);
        // Read on every open rather than cached for the session: activating a power scheme can
        // carry a different core preference with it, so a value read once would go stale silently.
        var hybridCores = new HybridCoreSelection(_power.HybridCores, _previewOnly);
        _overlay.AttachHybridCores(hybridCores);
        RefreshWindowsPolicies(_overlay);
        if (_powerPresets is not null)
        {
            var presets = new DevicePowerPresetSelection(_powerPresets, _previewOnly, _powerAssignments);
            _overlay.AttachPowerPresets(presets);
        }

        powerSchemes.Changed += () =>
        {
            if (powerSchemes is { Busy: false, ActiveId: not null } && ReferenceEquals(_overlayViewModel, vm))
            {
                RefreshPowerTimeouts(vm);
            }
        };
        var constructDone = Stopwatch.GetTimestamp();
        _overlay.AttachDeviceBridge(_sources.Device);
        _overlay.AttachGraphicsSource(_sources.Graphics);
        _overlay.AttachDevicePrerequisites(_sources.DevicePrerequisites);
        _overlay.AttachCommonPlugins(_sources.CommonPlugins);
        _overlay.AttachGameLibrary(_sources.GameLibrary);
        _overlay.AttachThemes(_sources.Themes);
        _overlay.AttachAnimations(_sources.Animations);
        _overlay.AttachSounds(_sources.Sounds);
        _overlay.AttachArtwork(_sources.Artwork);
        _overlay.AttachPerformanceSource(_sources.Performance);
        _overlay.SetPins(_pins);
        _overlay.PinToggleRequested += OnPinToggleRequested;
        _overlay.WindowPicked += PickWindow;
        _overlay.TrayIconActivated += OnTrayIconActivated;
        _overlay.RadioPanelRequested += ShowRadioPanel;
        _overlay.AudioPanelRequested += ShowAudioPanel;
        _overlay.EjectPanelRequested += ShowEjectPanel;
        Log.Info("Quick access shown (Open apps snapshot queued).");
        WireOverlayRequests(_overlay, vm);
        var openedOverlay = _overlay;
        openedOverlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_overlay, openedOverlay))
            {
                OnOverlayClosed();
            }
        };

        _overlay.AttachFormatManager(FormatManager);
        _overlay.PowerMenuRequested += TogglePowerMenu;
        _overlay.SurfaceClosed += () =>
        {
            if (_powerMenuOnly && _overlay is { HasActiveSurface: false })
            {
                CloseOverlay();
            }
        };

        _navigation = OverlayInput.Create(_overlay, _gamepad, OnOverlayBack, IsNintendoLayout);
        // Internal text entry shares this window and its single navigation owner.
        // Registered while the overlay owns navigation.
        _gamepad.Start();
        ClaimUiSurface(QuickAccessSurface);
        _overlay.Show();
        // Game-Bar-style: the game stops receiving input while the panel is up.
        // Safe because the Steam Input lease keeps the pad readable despite focus.
        _overlay.Activate();

        var showDone = Stopwatch.GetTimestamp();
        LogOpenTimings(openStarted, setupDone, constructDone, showDone);
        RefreshWakeLockIndicator();
        StartWakeLockRefresh();
        StartSwitcherRefresh();
    }

    /// <summary>Brings the open sheet back to the front, cancelling a deferred close in progress.</summary>
    /// <param name="overlay">The sheet that is still open.</param>
    private void ReactivateOpenOverlay(OverlayWindow overlay)
    {
        overlay.IsEnabled = true;
        _navigation?.IsEnabled = true;
        if (_pendingClose is not null)
        {
            // Re-summoned inside the 150 ms deferred close: cancel the pending
            // Close() and keep the window — otherwise the timer would destroy
            // the just-reactivated panel and release its lease under it.
            _pendingClose.Dispose();
            _pendingClose = null;
            // The action that requested the close was abandoned with it: a
            // suppressed focus restore must not stay latched for the rest of
            // this panel's life; see the focus-restore finding in docs\overlay-and-input.md.
            _suppressFocusRestore = false;
            Log.Info("Overlay re-shown during deferred close — pending close cancelled.");
        }

        if (_overlayViewModel is not null)
        {
            _overlayViewModel.WarningText = _pendingWarning;
            // Recompute what the fresh-open path computes — Steam may have died
            // or the desktop may have changed while the panel stayed open.
            _overlayViewModel.ExplorerRunning = ExplorerControl.IsDesktopShellRunning();
            _overlayViewModel.HomeAppAlive = _monitor?.IsAlive ?? false;
            _overlayViewModel.KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off;
            _overlayViewModel.KeepAwakeDownloadActive = _keepAwake?.DownloadHold ?? false;
            RefreshPowerTimeouts(_overlayViewModel);
            RefreshWakeLockIndicator();
            StartWakeLockRefresh();
        }

        RefreshSwitcherEntries();
        if (!_uiSurfaces.Contains(QuickAccessSurface))
        {
            ClaimUiSurface(QuickAccessSurface);
        }

        overlay.ResetForResummon();
        overlay.Activate();
    }

    /// <summary>Connects the sheet's requests to the session actions they start.</summary>
    /// <param name="overlay">The sheet being opened.</param>
    /// <param name="vm">Its view model, which some requests update.</param>
    private void WireOverlayRequests(OverlayWindow overlay, OverlayViewModel vm)
    {
        overlay.HomeAppRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            _modes.StartOrFocusSteam();
        };
        overlay.DesktopRequested += () =>
        {
            // Belt and braces with the hidden row: a preview surface must never run a
            // real transition, and this process has no recovery layer if it did.
            if (_previewOnly)
            {
                Log.Info("Mode switch ignored — this is a preview surface.");
                return;
            }

            // Mid-transition (boot takeover, or a switch already running) the
            // explorer state is in flux — acting on it would start a second,
            // conflicting transition (device-observed 2026-08-07).
            if (_modes.TransitionInProgress)
            {
                Log.Info("Mode switch ignored — an explorer transition is in progress.");
                return;
            }

            var explorerRunning = ExplorerControl.IsDesktopShellRunning();
            _suppressFocusRestore = true;
            CloseOverlay();
            if (explorerRunning)
            {
                _modes.EnterGameMode();
            }
            else
            {
                _modes.EnterDesktopMode();
            }
        };
        overlay.ExitBigPictureRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            SessionModes.ExitBigPicture();
        };
        overlay.CloseLauncherRequested += () =>
        {
            _modes.CloseSteam();
            vm.HomeAppAlive = false;
        };
        overlay.PowerActionRequested += action =>
        {
            // The sheet already dismissed itself. A preview (Settings' Test sheet or
            // --overlay-test) demonstrates the row and never acts on the machine, and
            // a controller the session gave no power port (tests) has nothing to call.
            if (_previewOnly || RequestPowerAction is not { } request)
            {
                Log.Info($"Power action {action} ignored — this surface does not act on the machine.");
                return;
            }

            request(action);
        };
        overlay.KeepAwakeSelected += mode =>
        {
            _keepAwake?.SetManualMode(mode);
            // Also reconcile a refused request whose actual mode did not change:
            // the view model then emits no property notification of its own.
            vm.KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off;
            overlay.RefreshPowerEditors(vm);
        };
        overlay.PowerTimeoutSelected += async (kind, seconds) =>
        {
            if (_previewOnly)
            {
                return;
            }

            // Scheme writes share a gate with the profile selector. Waiting for it must not
            // block input. Explicit selections do not require a prior read.
            // Enqueue on the UI thread, before yielding, to preserve explicit choice order
            // across rapid selections and close/reopen. A failed predecessor is not retried.
            var write = _powerTimeoutWrite.ContinueWith(_ =>
            {
                // The session's owner skips display presets Steam's screensaver forbids and tells
                // Steam's Screensaver settings about the change.
                return _displayTimeouts?.Select(kind, seconds) == true;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _powerTimeoutWrite = write;
            try
            {
                var written = await write;
                if (written && ReferenceEquals(_overlayViewModel, vm))
                {
                    PublishPowerTimeouts(vm, _displayTimeouts?.ObservedValues
                                             ?? new Dictionary<PowerTimeoutKind, int?> { [kind] = seconds });
                }
            }
            catch (Exception ex)
            {
                Log.Error("The selected power timeout could not be applied", ex);
            }
        };
        // Off the UI thread: the elevated one-shot blocks for as long as its consent prompt is
        // on screen, and a frozen sheet holding the Steam Input lease reads as a hang.
        overlay.UacPromptsRequested += async disable =>
        {
            try
            {
                await Task.Run(() => UacSettings.RequestChange(disable));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Could not change UAC prompts", ex);
            }

            RefreshWindowsPolicies(overlay);
        };
        overlay.LockOnWakeRequested += async disable =>
        {
            try
            {
                await Task.Run(() => LockScreenSettings.RequestChange(disable));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Could not change lock on wake", ex);
            }

            RefreshWindowsPolicies(overlay);
        };
        overlay.TaskManagerRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            StartTaskManager();
        };
        overlay.SettingsRequested += () =>
        {
            // A preview sheet hides the row; this is the belt to that brace.
            if (OpenSettings is not { } openSettings)
            {
                return;
            }

            _suppressFocusRestore = true;
            // Managed capture is claimed for Settings before the deferred close below ends the
            // sheet's claim. Only a new claim is released with the window, so a second request for
            // the one Settings window neither claims nor releases twice. The window itself claims
            // the Steam Input lease as it activates, also before that deferred close.
            var claimed = !_uiSurfaces.Contains(SettingsSurface);
            if (claimed)
            {
                ClaimUiSurface(SettingsSurface);
            }

            CloseOverlay();
            // A shell session normally has no main window. Opening settings in this
            // process keeps quick access responsive and avoids starting a second shell.
            Dispatcher.UIThread.Post(() => _ = HandOffAsync());

            async Task HandOffAsync()
            {
                try
                {
                    var settings = await openSettings();
                    if (claimed)
                    {
                        settings.Closed += (_, _) => ReleaseUiSurface(SettingsSurface);
                    }
                }
                catch (Exception ex)
                {
                    if (claimed)
                    {
                        ReleaseUiSurface(SettingsSurface);
                    }

                    Log.Error("Settings handoff window could not open", ex);
                }
            }
        };
        // Native file pickers retain their own navigation while they are visible.
        overlay.SystemDialogActive += active =>
        {
            if (!ReferenceEquals(_overlay, overlay))
            {
                return;
            }

            if (active)
            {
                _dialogPriorNavigation = _navigation?.IsEnabled ?? false;
            }

            _navigation?.IsEnabled = !active && _dialogPriorNavigation;
        };
        // Dismiss never refocuses anything: Windows hands the foreground back to
        // the previous window on close. An explicit refocus-on-dismiss once yanked
        // Steam over an app the user had deliberately cycled to.
        overlay.Dismissed += CloseOverlay;
    }

    /// <summary>Releases everything the sheet held once its window has closed.</summary>
    private void OnOverlayClosed()
    {
        ReleaseUiSurface(QuickAccessSurface);
        _pendingClose?.Dispose();
        _pendingClose = null;
        // Detach surfaces before disposing the live managers they observe.
        _overlay?.CloseAllSurfaces();
        StopSwitcherRefresh();
        _switcherViewModel = null;
        _systemStatus?.Dispose();
        _systemStatus = null;
        // Free the rasterized icons with the sheet; the next open re-resolves.
        _iconCache?.Clear();
        // Same for the cached Steam pid set: the next session starts fresh.
        _steamPidsAtUtc = default;
        // Give Steam its pad back the moment the sheet is gone. This ends only
        // the sheet's own claim: a Settings window opened from it claimed the
        // lease in Opened, before this deferred close, and keeps it live.
        ReleaseSteamInputLease();

        var reopenForWarning = _reopenOverlayForWarning;
        _reopenOverlayForWarning = false;
        _navigation?.Dispose();
        _navigation = null;
        StopWakeLockRefresh();
        _powerMenuOnly = false;
        // Keep polling if the controller chord still needs it.
        if (!(_activationEnabled && _config.GamepadChord.Enabled && _config.GamepadChord.Buttons != 0))
        {
            _gamepad.Stop();
        }

        _overlay = null;
        _overlayRequiresSteamLease = false;
        _overlayViewModel = null;
        // Game mode only: call back the window that was focused before the
        // panel opened (exclusive-fullscreen games sit minimized by now).
        if (!_suppressFocusRestore && _restoreFocusTo != 0 && !ExplorerControl.IsDesktopShellRunning())
        {
            Log.Info("Restoring previously focused window.");
            WindowFinder.BringToForeground(_restoreFocusTo);
        }

        _restoreFocusTo = 0;
        ShowTouchEdges();
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
        List<string> pins = [.. _pins];
        if (!pins.Remove(id))
        {
            pins.Add(id);
        }

        _pins = pins;
        _overlay?.SetPins(pins);
        Log.Info($"Quick access pins: {string.Join(", ", pins)}.");
        if (_previewOnly)
        {
            return;
        }

        string[] snapshot = [.. pins];
        PinWrites = PinWrites.ContinueWith(_ => SavePins(snapshot), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Stores one pin list; a failure is logged and the next toggle still saves its own list.</summary>
    private void SavePins(string[] pins)
    {
        try
        {
            _store.Update(config =>
            {
                if (config.QuickAccessPins.SequenceEqual(pins, StringComparer.Ordinal))
                {
                    return false;
                }

                config.QuickAccessPins = [.. pins];
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save quick access pins: {ex.Message}");
        }
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
        if (_disposed || _overlay is not null)
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
                static window => window.DockToTopEdge(),
                UiScale())
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
        if (_overlay is null)
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
        _overlay?.SelectDeviceDestination();
    }

    internal bool ShowBluetoothPanel()
    {
        if (_disposed)
        {
            return false;
        }

        ShowOverlay();
        ShowRadioPanel(true);
        return _overlay?.HasActiveSurface == true;
    }

    private void ShowRadioPanel(bool bluetooth)
    {
        if (_systemStatus is not null)
        {
            _overlay?.ShowRadioSurface(new RadioPanel(_systemStatus.Radios, bluetooth));
        }
    }

    private void ShowAudioPanel()
    {
        if (_systemStatus is not null)
        {
            _overlay?.ShowAudioSurface(new AudioPanel(_systemStatus.Audio, _audioProfiles));
        }
    }

    private void ShowEjectPanel()
    {
        if (_systemStatus is not null)
        {
            _overlay?.ShowEjectSurface(new EjectPanel(_systemStatus.Drives));
        }
    }

    /// <summary>
    ///     The desktop-DPI factor for WSGM surfaces. The boost exists ONLY
    ///     to compensate game mode's forced 100% display scaling — in desktop mode
    ///     the display already runs at the user's real scaling and Avalonia applies
    ///     it, so boosting again would double up (device-reported: surfaces rendered
    ///     huge on a 100% desktop when the recommended-scale fallback fired there).
    /// </summary>
    private double UiScale()
    {
        return UiScale(ExplorerControl.IsDesktopShellRunning());
    }

    private double UiScale(bool explorerRunning)
    {
        return explorerRunning
            ? 1.0
            : DisplayScale.GetUiScalePercent(_config) / 100.0;
    }

    /// <summary>
    ///     The single idiom for delayed UI-thread work in this controller
    ///     (deferred close, Task Manager focus polling). Runs the action
    ///     on the UI thread after the delay; dispose the returned handle to cancel.
    ///     UI-thread callers only — overlay events and SteamMonitor's tick already are.
    /// </summary>
    private static IDisposable RunOnUiThreadAfter(TimeSpan delay, Action action)
    {
        return DispatcherTimer.RunOnce(action, delay);
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
        if (_overlay is null || _pendingClose is not null)
        {
            return;
        }

        // Deferred: a touch tap's DefWindowProc promotion delivers a synthesized
        // mouse click AFTER this dispatch. If the window were already destroyed,
        // that click would land on whatever sits underneath (user-reproduced).
        // Kept open a beat, the window's own hook eats the synthesized click.
        // ShowOverlay cancels this via _pendingClose when re-summoned in time.
        _overlay.IsEnabled = false;
        _navigation?.IsEnabled = false;
        _pendingClose = RunOnUiThreadAfter(TouchInput.CloseGrace, () =>
        {
            _pendingClose = null;
            _overlay?.Close();
        });
    }

    /// <summary>UI sink for the coordinator's Steam start failures.</summary>
    private void WarnOrReopen(string warning)
    {
        SetWarning(warning);
        // The request normally closes the overlay first. If that close is
        // asynchronous, its Closed handler will recreate it with the error.
        if (_overlay is null)
        {
            ShowOverlay();
        }
        else
        {
            _reopenOverlayForWarning = true;
        }
    }
}
