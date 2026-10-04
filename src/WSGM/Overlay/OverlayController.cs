using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Overlay;

/// <summary>
///     Owns the overlay activation surfaces (hotkey, raw-input touch swipes) and the
///     focus-taking WSGM surface itself: the quick access sheet (ShowOverlay), which
///     also carries the Open apps strip, the tray icons and the status pills with
///     their radio/audio/eject panels. One controller
///     owns all of it because it shares every piece of invariant-critical state: the
///     Steam Input lease, the touch-swipe disarm/re-arm cycle,
///     the gamepad service, and the focus-restore discipline.
/// </summary>
public sealed partial class OverlayController : IDisposable
{
    private const string QuickAccessSurface = "quick-access";
    private const string SettingsSurface = "settings";
    private readonly AudioProfileService? _audioProfiles;
    private readonly GamepadChordWatcher _chordWatcher;

    /// <summary>
    ///     The session's display-off timeouts, shared with Steam's Screensaver settings, or null when the
    ///     controller has no session behind it.
    /// </summary>
    private readonly DisplayTimeouts? _displayTimeouts;

    private readonly GamepadService _gamepad = new();
    private readonly HotkeyService _hotkey;
    private readonly KeepAwakeService? _keepAwake;

    /// <summary>
    ///     This controller's identity in the blocker's process-wide ownership
    ///     set. Per instance on purpose: a replacement controller (the Settings preview's
    ///     "Test panel" pressed twice) claims the lease under its own name, so the
    ///     outgoing controller's release cannot drop the live surface's lease.
    /// </summary>
    private readonly string _leaseOwner = SteamInputBlocker.NewOwner("overlay-controller");

    private readonly SessionModes _modes;
    private readonly SteamMonitor? _monitor;
    private readonly DevicePowerAssignments? _powerAssignments;
    private readonly DevicePowerPresets? _powerPresets;
    private readonly bool _previewOnly;

    /// <summary>
    ///     The session's audio manager, shared with the sheet's status pills rather than owned.
    /// </summary>
    /// <remarks>
    ///     Null in overlay-test, where no session owns one and the cluster creates its own.
    /// </remarks>
    private readonly AudioManager? _sessionAudio;

    /// <summary>
    ///     The session's removable-drive manager, shared with the sheet's eject pill rather than owned.
    /// </summary>
    /// <remarks>
    ///     Shared for the same reason audio is: Steam's revived storage pages answer while the overlay
    ///     is closed, and a manager the sheet disposes cannot serve them. Two managers would also
    ///     enumerate every volume twice and could disagree about what is still ejectable.
    /// </remarks>
    private readonly RemovableDriveManager? _sessionDrives;

    /// <summary>
    ///     The session's radio manager, shared with the sheet's status pills rather than owned.
    /// </summary>
    /// <remarks>
    ///     Null in overlay-test, where no session owns one and the cluster creates its own.
    /// </remarks>
    private readonly RadioManager? _sessionRadios;

    private readonly OverlaySources _sources;

    /// <summary>What every navigation surface here subscribes to.</summary>
    /// <remarks>
    ///     The surfaces take the router rather than <see cref="GamepadService" /> so they see whichever
    ///     source is delivering. With controller management off this is SDL, exactly as before. With it
    ///     on, WSGM's own UI can finally be driven by the controls SDL cannot see on a handheld — the
    ///     rear paddles, Quick Access, and the trackpad clicks.
    ///     <para>
    ///         The chord watcher deliberately stays on the raw SDL service. The chord is what opens the
    ///         overlay, so it has to keep working when the managed source is not running, and it is the one
    ///         thing that must not change behaviour with the source.
    ///     </para>
    /// </remarks>
    private readonly HashSet<string> _uiSurfaces = new(StringComparer.Ordinal);


    private AppConfig _config;
    private bool _dialogPriorNavigation;


    private bool _disposed;


    private SdFormatManager? _formatManager;

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
    private IDisposable? _pendingSteamRelaunch;
    private string _pendingWarning = "";
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
    public OverlayController(AppConfig config, SteamMonitor? monitor, SessionModes modes,
        KeepAwakeService? keepAwake = null, bool previewOnly = false)
        : this(config, monitor, modes, keepAwake, previewOnly, null)
    {
    }

    internal OverlayController(AppConfig config, SteamMonitor? monitor, SessionModes modes,
        KeepAwakeService? keepAwake, bool previewOnly, OverlaySources? sources,
        AudioManager? audio = null,
        AudioProfileService? audioProfiles = null,
        RadioManager? radios = null,
        DevicePowerPresets? powerPresets = null,
        DevicePowerAssignments? powerAssignments = null,
        RemovableDriveManager? drives = null,
        SdFormatManager? formats = null,
        DisplayTimeouts? displayTimeouts = null)
    {
        _sources = sources ?? new OverlaySources();
        _displayTimeouts = displayTimeouts;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed += OnDisplayTimeoutsChanged;
        }

        _powerPresets = powerPresets;
        _powerAssignments = powerAssignments;
        _sessionAudio = audio;
        _audioProfiles = audioProfiles;
        _sessionRadios = radios;
        _sessionDrives = drives;
        if (formats is not null)
        {
            _formatManager = formats;
            _formatManager.Finished += OnFormatFinished;
        }

        _config = config;
        _monitor = monitor;
        _modes = modes;
        _keepAwake = keepAwake;
        _previewOnly = previewOnly;
        if (_keepAwake is not null)
        {
            _keepAwake.StateChanged += OnKeepAwakeStateChanged;
        }

        _modes.SteamStartFailed += WarnOrReopen;
        SteamInputBlocker.RecoveryWarningRaised += OnSteamInputRecoveryWarning;

        _hotkey = new HotkeyService(MessageWindow.Create());
        _hotkey.Pressed += ShowOverlay;
        _hotkey.Apply(config.Hotkey);

        // Controller chord: needs polling even with no WSGM window on screen.
        _chordWatcher = new GamepadChordWatcher(_gamepad, config.GamepadChord);
        _chordWatcher.Triggered += ShowOverlay;
        if (config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0)
        {
            _gamepad.Start();
        }

        ApplyGestures(config.Gestures);

        if (_monitor is not null)
        {
            _monitor.SteamExited += OnSteamExited;
        }
    }

    internal Func<CancellationToken, Task<bool>>? ShowOnScreenKeyboard { get; set; }
    internal GameWindowReturn? GameReturn { get; set; }


    /// <summary>
    ///     The shared removable-storage format manager backing the Tools
    ///     tab's Format SD Card / Add Steam Library flow. Created on first use and
    ///     kept for the controller's lifetime so a format survives the overlay
    ///     closing; a completion reached while the overlay is closed surfaces
    ///     through the warning bar on the next open.
    /// </summary>
    private SdFormatManager FormatManager
    {
        get
        {
            if (_formatManager is not null)
            {
                return _formatManager;
            }

            _formatManager = new SdFormatManager();
            _formatManager.Finished += OnFormatFinished;
            return _formatManager;
        }
    }

    /// <summary>Whether the power menu currently consumes short power-button requests.</summary>
    public bool PowerMenuOpen => _overlay?.IsPowerMenuOpen == true;

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
        // here: the only caller of this Dispose is the Settings preview controller,
        // and retracting would tear the LIVE session's tabs out of Big Picture.
        // ShellSession owns that teardown and awaits it in ApplyCefMasterSwitch.
        AttachTrayHost(null);
        _modes.SteamStartFailed -= WarnOrReopen;
        SteamInputBlocker.RecoveryWarningRaised -= OnSteamInputRecoveryWarning;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed -= OnDisplayTimeoutsChanged;
        }

        if (_keepAwake is not null)
        {
            // The service belongs to ShellSession; only the subscription is ours.
            _keepAwake.StateChanged -= OnKeepAwakeStateChanged;
        }

        if (_monitor is not null)
        {
            _monitor.SteamExited -= OnSteamExited;
        }

        _pendingSteamRelaunch?.Dispose();
        _hotkey.Dispose();
        _chordWatcher.Dispose();

        // Before the service it subscribes to, so the unsubscribe lands on a live object.
        _gamepad.Dispose();
        DisposeTouchEdges();
        StopSwitcherRefresh();
        if (_overlay is not null)
        {
            // This controller owes a lease release (its overlay is open / pending
            // close). Fire it NOW, not in the deferred Closed handler 150 ms from
            // here: a replacement controller (Test panel pressed again) may acquire
            // a lease in between, and a late release would leave its live overlay
            // without input. The blocker ignores a claim that already ended, so the
            // Closed handler's release is a no-op afterwards.
            ReleaseSteamInputLease();
        }

        // Close through the same deferred path as every dismissal: an immediate
        // Close() would skip the 150 ms grace and bring back the ghost clicks the
        // deferral exists for. When Dispose runs during process exit the
        // dispatcher may stop pumping before the 150 ms lands and the Close()
        // never runs — deliberately fine: the lease was already released
        // synchronously above, and process exit destroys the window anyway.
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
        if (_overlay?.CloseActiveSurface() == true || _overlay?.TryCancelSubView() == true)
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
        _overlay?.SetBlurRadius(config.OverlayBlurRadius);
        _sources.CommonPlugins?.ApplyPins(config.PluginWidgetPins);
        // The master CEF switch is owned by ShellSession, which retracts injected UI
        // before closing it — setting it here as well would cut that retraction off.
        // UI-thread only: this writes view-model state, control titles and the
        // gamepad's DispatcherTimer with no marshalling of its own. ShellSession's
        // debounced config watcher already posts it; the Post below only keeps the
        // accent re-apply safe for this public entry point.
        Dispatcher.UIThread.Post(() =>
            AccentPalette.Apply(Application.Current!, AccentPalette.Parse(config.AccentColor)));
        _modes.ApplyConfig(config);
        _hotkey.Apply(config.Hotkey);
        _chordWatcher.ApplyConfig(config.GamepadChord);
        var chordActive = config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0;
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
            _overlay?.SetPins(config.QuickAccessPins);
        }

        if (_overlay is not null && _overlayRequiresSteamLease)
        {
            AcquireSteamInputLease();
        }

        // Applied on reload so raising verbosity to reproduce something does not need a restart —
        // the shell process that would have to be restarted is the one being diagnosed.
        Log.SetVerbosity(config.LogVerbosity);
        Log.Debug($"Config reloaded at {config.LogVerbosity} verbosity.");
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
        _overlay = new OverlayWindow(vm, switcher, _systemStatus, UiScale(explorerRunning),
            WindowCenter(_restoreFocusTo));
        _overlay.SetBlurRadius(_config.OverlayBlurRadius);
        if (_sources.Brightness is { } brightness)
        {
            _overlay.AttachBrightness(brightness);
        }

        if (_sources.ManualTdp is { } manual)
        {
            _overlay.AttachManualTdp(manual);
        }

        _overlay.OnScreenKeyboardRequested += async () => await RequestOnScreenKeyboardAsync();
        var powerSchemes = new PowerSchemeSelection(PowerSchemes.Windows,
            id => ConfigStore.Mutate(config => config.LastSelectedPowerSchemeId = id), _previewOnly);
        _overlay.AttachPowerSchemes(powerSchemes);
        // Read on every open rather than cached for the session: activating a power scheme can
        // carry a different core preference with it, so a value read once would go stale silently.
        var hybridCores = new HybridCoreSelection(HybridCores.Windows, _previewOnly);
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
        _overlay.SetPins(_config.QuickAccessPins);
        _overlay.PinToggleRequested += OnPinToggleRequested;
        _overlay.WindowPicked += PickWindow;
        _overlay.TrayIconActivated += OnTrayIconActivated;
        _overlay.RadioPanelRequested += ShowRadioPanel;
        _overlay.AudioPanelRequested += ShowAudioPanel;
        _overlay.EjectPanelRequested += ShowEjectPanel;
        Log.Info("Quick access shown (Open apps snapshot queued).");
        WireOverlayRequests(_overlay, vm);
        _overlay.Closed += (_, _) => OnOverlayClosed();

        _overlay.AttachFormatManager(FormatManager);
        _overlay.PowerMenuRequested += TogglePowerMenu;
        _overlay.SurfaceClosed += () =>
        {
            if (_powerMenuOnly && _overlay is { HasActiveSurface: false })
            {
                CloseOverlay();
            }
        };

        var overlay = _overlay;
        _navigation = new GamepadNavigation(_gamepad, _overlay, OnOverlayBack,
            IsNintendoLayout,
            () => overlay.DefaultFocusTarget,
            focused =>
            {
                if (overlay.IsPowerMenuOpen)
                {
                    overlay.CloseActiveSurface();
                }
                else if (!overlay.HasActiveSurface)
                {
                    overlay.RequestSecondaryAction(focused);
                }
            },
            () =>
            {
                if (!overlay.NavigateSurfaceTab(false))
                {
                    overlay.SelectPreviousTab();
                }
            },
            () =>
            {
                if (!overlay.NavigateSurfaceTab(true))
                {
                    overlay.SelectNextTab();
                }
            },
            _ =>
            {
                if (!overlay.HasActiveSurface)
                {
                    overlay.CycleNextApp();
                }
            },
            direction => !overlay.HasActiveSurface && overlay.NavigateWorkspace(direction),
            true, () => overlay.ActiveSurfaceNavigationRoot);
        // Internal text entry shares this window and its single navigation owner.
        // Registered while the overlay owns navigation.
        KeyboardService.Handler = OpenKeyboard;
        _gamepad.Start();
        ClaimUiSurface(QuickAccessSurface);
        try
        {
            _overlay.Show();
            // Game-Bar-style: the game stops receiving input while the panel is up.
            // Safe because the Steam Input lease keeps the pad readable despite focus.
            _overlay.Activate();
        }
        catch
        {
            // Everything above this try (lease, navigation, gamepad start, keyboard
            // handler) is already live, and a window that failed to show never raises
            // Closed to release it, so the UI surface claim alone left Quick Access
            // wedged and the lease held for the rest of the session. Run the same
            // teardown the Closed handler would have, so the next ShowOverlay()
            // rebuilds instead of reactivating a phantom window.
            OnOverlayClosed();
            throw;
        }

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
            // Scheme writes share a gate with the profile selector. Waiting for it must not
            // block input. Explicit selections do not require a prior read.
            // Enqueue on the UI thread, before yielding, to preserve explicit choice order
            // across rapid selections and close/reopen. A failed predecessor is not retried.
            var write = _powerTimeoutWrite.ContinueWith(_ =>
            {
                // The session's owner skips display presets Steam's screensaver forbids and tells
                // Steam's Screensaver settings about the change.
                if (_displayTimeouts is not null)
                {
                    return _displayTimeouts.Select(kind, seconds);
                }

                lock (PowerSchemes.MutationGate)
                {
                    return PowerTimeouts.Write(kind, seconds);
                }
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
            await Task.Run(() => UacSettings.RequestChange(disable));
            RefreshWindowsPolicies(overlay);
        };
        overlay.LockOnWakeRequested += async disable =>
        {
            await Task.Run(() => LockScreenSettings.RequestChange(disable));
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
            _suppressFocusRestore = true;
            // Settings claims the Steam Input lease as it opens, before the deferred
            // close below ends this sheet's claim, so Steam's controller stays blocked
            // across the switch with no release/re-inject churn.
            var settings = new SettingsWindow(true);
            ClaimUiSurface(SettingsSurface);
            settings.Closed += (_, _) => ReleaseUiSurface(SettingsSurface);
            CloseOverlay();
            // A shell session normally has no main window. Opening settings in this
            // process keeps quick access responsive and avoids starting a second shell.
            // gameModeSurface: the window takes over as the on-screen surface, else
            // Steam's desktop profile grabs the pad over Settings.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    settings.Show();
                }
                catch (Exception ex)
                {
                    ReleaseUiSurface(SettingsSurface);
                    Log.Error("Settings handoff window could not open", ex);
                }
            });
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
        KeyboardService.Handler = null;
        _powerMenuOnly = false;
        // Keep polling if the controller chord still needs it.
        if (!(_config.GamepadChord.Enabled && _config.GamepadChord.Buttons != 0))
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
    ///     Pins or unpins a row on the Quick access root: the in-memory config keeps
    ///     the sheet consistent immediately; the file write happens off-thread and the
    ///     config watcher's reload then hands back the same list. A preview surface
    ///     (Settings' Test sheet) never writes.
    /// </summary>
    private void OnPinToggleRequested(string id)
    {
        var pins = new List<string>(_config.QuickAccessPins);
        if (!pins.Remove(id))
        {
            pins.Add(id);
        }

        _config.QuickAccessPins = pins;
        _overlay?.SetPins(pins);
        Log.Info($"Quick access pins: {string.Join(", ", pins)}.");
        if (_previewOnly)
        {
            return;
        }

        var snapshot = pins.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                ConfigStore.Mutate(config => config.QuickAccessPins = [.. snapshot]);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not save quick access pins: {ex.Message}");
            }
        });
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
                new OverlayViewModel(),
                new AppSwitcherViewModel(),
                status,
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
    ///     (deferred close, auto-relaunch, Task Manager focus polling). Runs the action
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
