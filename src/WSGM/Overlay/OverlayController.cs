using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Overlay;

/// <summary>
///     Owns the overlay activation surfaces (hotkey, raw-input touch swipes) and the
///     focus-taking WSGM surface itself: the quick access sheet (ShowOverlay), which
///     also carries what the bottom taskbar used to — the Open apps strip, the tray
///     icons and the status pills with their radio/audio/eject panels. One controller
///     owns all of it because it shares every piece of invariant-critical state: the
///     Steam Input lease, the touch-swipe disarm/re-arm cycle,
///     the gamepad service, and the focus-restore discipline.
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>What an edge swipe opens (routing result).</summary>
    public enum SwipeAction
    {
        /// <summary>The swipe is ignored.</summary>
        None,

        /// <summary>The quick access sheet opens.</summary>
        QuickAccess,

        /// <summary>The quick access sheet opens with focus on its Open apps strip.</summary>
        QuickAccessApps,

        /// <summary>Steam Big Picture's left-side Steam menu opens.</summary>
        SteamMenu,

        /// <summary>Steam Big Picture's right-side Quick Access Menu opens.</summary>
        SteamQuickAccess
    }

    private const string QuickAccessSurface = "quick-access";
    private const string SettingsSurface = "settings";
    private static int _nextLeaseOwnerId;
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
    private readonly string _leaseOwner =
        $"overlay-controller#{Interlocked.Increment(ref _nextLeaseOwnerId)}";

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
    ///     The session's removable-drive manager, or null when this controller's taskbar owns one.
    /// </summary>
    /// <remarks>
    ///     Shared for the same reason audio is: Steam's revived storage pages answer while the overlay
    ///     is closed, and a manager the taskbar disposes cannot serve them. Two managers would also
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
    private readonly UiInputRouter _uiInput;

    private readonly HashSet<string> _uiSurfaces = new(StringComparer.Ordinal);


    private bool _closePending;
    private AppConfig _config;
    private bool _dialogPriorNavigation;


    private bool _disposed;


    private SdFormatManager? _formatManager;

    /// <summary>
    ///     Set for the one overlay close that opens the settings window: the
    ///     lease is handed to Settings rather than released, so Steam's controller is
    ///     not dropped and re-revoked across the switch.
    /// </summary>
    private bool _handoffLease;

    private WindowIconCache? _iconCache;
    private CancellationTokenSource? _keyboardRequestCancellation;
    private bool _keyboardRequestPending;

    private string? _lastWakeLockError;
    private Task? _leaseAcquireTask;
    private Task? _leaseReleaseTask;

    private bool _leaseReleased = true;
    private GamepadNavigation? _navigation;
    private OverlayWindow? _overlay;
    private bool _overlayRequiresSteamLease;
    private OverlayViewModel? _overlayViewModel;
    private IDisposable? _pendingClose;

    private IDisposable? _pendingTopmostRestore;
    private IDisposable? _pendingTrim;
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

    private SettingsWindow? _settingsHandoffWindow;

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

        _uiInput = new UiInputRouter(_gamepad);

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

    internal Func<SteamControllerHandoff?> SteamOwnership { get; set; } = () => null;
    internal Func<CancellationToken, Task<bool>>? ShowOnScreenKeyboard { get; set; }
    internal GameWindowReturn? GameReturn { get; set; }

    /// <summary>Opens a Game Library page inside Steam, answering whether Steam took the route.</summary>
    /// <remarks>
    ///     Supplied by the session, which owns the Steam transport and the artwork source, and set
    ///     after construction because the Steam UI host is built after the overlay.
    /// </remarks>
    internal Func<GameLibrarySteamTarget, CancellationToken, Task<bool>>? OpenInSteam { get; set; }

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

        _hotkey.Dispose();
        _chordWatcher.Dispose();

        // Before the service it subscribes to, so the unsubscribe lands on a live object.
        _uiInput.Dispose();
        _gamepad.Dispose();
        DisposeTouchEdges();
        StopSwitcherRefresh();
        if (_overlay is not null)
        {
            // This controller owes a lease release (its overlay is open / pending
            // close). Fire it NOW, not in the deferred Closed handler 150 ms from
            // here: a replacement controller (Test panel pressed again) may acquire
            // a lease in between, and a late release would leave its live overlay
            // without input. ReleaseSteamInputLease's guard makes the Closed
            // handler's release a no-op afterwards.
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

    private async Task RequestOnScreenKeyboardAsync()
    {
        if (_keyboardRequestPending || _disposed || _overlay is not { } window)
        {
            return;
        }

        _keyboardRequestPending = true;
        using var cancellation = new CancellationTokenSource();
        _keyboardRequestCancellation = cancellation;
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? sender, EventArgs args)
        {
            closed.TrySetResult();
        }

        window.Closed += OnClosed;
        try
        {
            CloseOverlay();
            await closed.Task.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            var shown = ShowOnScreenKeyboard is { } show
                        && await show(cancellation.Token);
            if (!shown && !_disposed && !cancellation.IsCancellationRequested)
            {
                WarnOrReopen("On-screen keyboard unavailable. Check Steam or Windows touch keyboard.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"On-screen keyboard request failed: {ex.Message}");
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                WarnOrReopen("On-screen keyboard could not be opened.");
            }
        }
        finally
        {
            window.Closed -= OnClosed;
            if (ReferenceEquals(_keyboardRequestCancellation, cancellation))
            {
                _keyboardRequestCancellation = null;
                _keyboardRequestPending = false;
            }
        }
    }

    /// <summary>Applies changed gesture settings without replacing the monitor.</summary>
    /// <param name="gestures">The new edge-swipe configuration.</param>
    private void ApplyGestures(GestureConfig gestures)
    {
        // Keep one recognizer owner across configuration changes.
        if (_touchSwipes is null)
        {
            _touchSwipes = new TouchSwipeMonitor();
            _touchSwipes.Triggered += OnSwipeTriggered;
        }

        _touchSwipes.Configure(gestures);

        // The open sheet disarms the edges: docked on the top edge, a re-arm would
        // read touches inside its header as top-edge swipes.
        if (_overlay is not null)
        {
            HideTouchEdges();
        }
        else
        {
            ShowTouchEdges();
        }
    }

    private void OnSwipeTriggered(ScreenEdge edge)
    {
        switch (DecideSwipe(edge, ExplorerControl.IsRunningInSession()))
        {
            case SwipeAction.QuickAccessApps:
                ShowOverlayOnOpenApps();
                break;
            case SwipeAction.QuickAccess:
                ShowOverlay();
                break;
            case SwipeAction.SteamMenu:
                Steam.TrySendBigPictureShortcut(BigPictureShortcut.SteamMenu);
                break;
            case SwipeAction.SteamQuickAccess:
                Steam.TrySendBigPictureShortcut(BigPictureShortcut.QuickAccess);
                break;
            case SwipeAction.None:
            default:
                Log.Info("Bottom swipe ignored in desktop mode (explorer's taskbar owns the edge).");
                break;
        }
    }

    /// <summary>
    ///     The pure edge-routing decision — the SteamOS map: left/right open
    ///     Steam's own menus, top opens WSGM's sheet, and bottom opens the sheet on its
    ///     Open apps strip in game mode but is IGNORED in desktop mode — explorer's
    ///     real taskbar owns that edge there, and falling back to the panel read as a
    ///     regression (device-reported).
    /// </summary>
    /// <param name="edge">The swiped screen edge.</param>
    /// <param name="explorerRunning">Whether the session currently has a desktop.</param>
    /// <returns>What the swipe opens, if anything.</returns>
    public static SwipeAction DecideSwipe(ScreenEdge edge, bool explorerRunning)
    {
        return edge switch
        {
            ScreenEdge.Left => SwipeAction.SteamMenu,
            ScreenEdge.Right => SwipeAction.SteamQuickAccess,
            ScreenEdge.Top => SwipeAction.QuickAccess,
            _ => explorerRunning ? SwipeAction.None : SwipeAction.QuickAccessApps
        };
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

    private void OnSteamExited()
    {
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.ShowOverlay:
                ShowOverlay();
                return;
            case SteamExitReaction.RelaunchBigPicture:
            case SteamExitReaction.RelaunchDesktop:
                Log.Info("Steam exited — auto-relaunching in 10 s.");
                RunOnUiThreadAfter(TimeSpan.FromMilliseconds(10_000), RelaunchSteamAfterExit);
                return;
            case SteamExitReaction.Ignore:
            default:
                Log.Info("Steam exited — leaving it closed.");
                return;
        }
    }

    /// <summary>
    ///     Re-decides at fire time: a config reload replaces <c>_config</c> wholesale, the
    ///     session may have changed mode, and the user may have closed Steam while the delay ran.
    /// </summary>
    private void RelaunchSteamAfterExit()
    {
        if (_disposed)
        {
            return;
        }

        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.RelaunchBigPicture:
                _modes.StartOrFocusSteam();
                return;
            case SteamExitReaction.RelaunchDesktop:
                _modes.EnsureSteamDesktop();
                return;
            case SteamExitReaction.Ignore:
            case SteamExitReaction.ShowOverlay:
            default:
                Log.Info("Auto-relaunch skipped: the session no longer wants Steam started.");
                return;
        }
    }

    /// <summary>
    ///     Reads the live session state the policy needs. Explorer's presence is the same
    ///     signal the overlay's own mode button uses to tell desktop from game mode.
    /// </summary>
    private SteamExitReaction DecideSteamExitReaction()
    {
        return SteamExitPolicy.Decide(
            !ExplorerControl.IsRunningInSession(),
            _config.SteamAutoRelaunch,
            _monitor?.Paused == true,
            _modes.SteamClosedByUser);
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
        vm.ShowLibraryTabs = config.Cef is { Enabled: true, LibraryTabs: true };
        vm.ShowCardManager = config.Cef is { Enabled: true, CardManager: true };
        vm.ShowSdCard = config.Cef is { Enabled: true, SdFormat: true };
        vm.ConfigureLaunchOptionsLive = config.Cef.Enabled;
        vm.InputLeaseUsesShim = config.SteamInputManagementEnabled;
    }

    /// <summary>
    ///     Reads the four idle timeouts from the active power scheme into the
    ///     Power tab's badges ("—" when the power API gives no answer), and says when Steam's
    ///     screensaver holds a display timeout up.
    /// </summary>
    private void RefreshPowerTimeouts(OverlayViewModel vm)
    {
        var timeouts = PowerTimeouts.ReadAll();
        vm.DisplayDcTimeout = Format(timeouts.DisplayDc);
        vm.DisplayAcTimeout = Format(timeouts.DisplayAc);
        vm.DisplayDcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayDc);
        vm.DisplayAcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayAc);
        vm.SleepDcTimeout = Format(timeouts.SleepDc);
        vm.SleepAcTimeout = Format(timeouts.SleepAc);
        vm.PowerTimeoutMinimums = Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => _displayTimeouts?.Minimum(kind));
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?>
        {
            [PowerTimeoutKind.DisplayDc] = timeouts.DisplayDc,
            [PowerTimeoutKind.DisplayAc] = timeouts.DisplayAc,
            [PowerTimeoutKind.SleepDc] = timeouts.SleepDc,
            [PowerTimeoutKind.SleepAc] = timeouts.SleepAc
        };
        return;

        static string Format(int? seconds)
        {
            return seconds is null ? "—" : PowerTimeouts.Describe(seconds.Value);
        }
    }

    /// <summary>
    ///     Mirrors keep-awake hold changes (poll loop or toggle, any thread)
    ///     into an open panel's view model.
    /// </summary>
    private void OnKeepAwakeStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _overlayViewModel is null || _keepAwake is null)
            {
                return;
            }

            _overlayViewModel.KeepAwakeManualMode = _keepAwake.ManualMode;
            _overlayViewModel.KeepAwakeDownloadActive = _keepAwake.DownloadHold;
        });
    }

    /// <summary>
    ///     Mirrors a display timeout chosen in Steam's Screensaver settings, or a new bound from
    ///     Steam's screensaver timeout, into an open panel's view model.
    /// </summary>
    private void OnDisplayTimeoutsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _overlayViewModel is { } vm)
            {
                RefreshPowerTimeouts(vm);
            }
        });
    }

    /// <summary>The display row's description: the plain one, or the bound Steam's screensaver sets.</summary>
    private string DisplayTimeoutDescription(PowerTimeoutKind kind)
    {
        return _displayTimeouts?.Minimum(kind) is { } minimum
            ? $"Idle time before the display turns off; at least {PowerTimeouts.Describe(minimum)} for Steam's screensaver"
            : OverlayViewModel.DisplayTimeoutDescription;
    }

    /// <summary>
    ///     Polls the system-wide power-request list into the Keep Awake row's
    ///     WakeWatch-style dot while the panel is open (~65 µs syscall, WakeWatch runs
    ///     it at 1 Hz permanently). Started per ShowOverlay, stopped with the panel.
    /// </summary>
    private void StartWakeLockRefresh()
    {
        if (_keepAwake is null)
        {
            return;
        }

        if (_wakeLockRefresh is null)
        {
            // Parameterless ctor + explicit Start (the 3-arg ctor auto-starts and
            // defeats IsEnabled guards — device-verified invariant).
            _wakeLockRefresh = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _wakeLockRefresh.Tick += (_, _) => RefreshWakeLockIndicator();
        }

        _wakeLockRefresh.Start();
    }

    private void StopWakeLockRefresh()
    {
        _wakeLockRefresh?.Stop();
    }

    private void RefreshWakeLockIndicator()
    {
        _ = RefreshWakeLockIndicatorAsync();
    }

    /// <summary>
    ///     Queries the power-request list on the pool and applies it on the UI thread. A tick
    ///     that arrives while a query is still running is skipped rather than queued.
    /// </summary>
    private async Task RefreshWakeLockIndicatorAsync()
    {
        if (_wakeLockQueryRunning || _disposed || _overlay is null || _overlayViewModel is null || _keepAwake is null)
        {
            return;
        }

        _wakeLockQueryRunning = true;
        try
        {
            var (entries, error) = await Task.Run(PowerRequestList.Query);
            if (_disposed || _overlay is not { } overlay || _overlayViewModel is not { } viewModel)
            {
                return;
            }

            if (error != _lastWakeLockError)
            {
                // Log transitions only — this ticks every 1.5 s while the panel is open.
                _lastWakeLockError = error;
                if (error is not null)
                {
                    Log.Warn($"Wake lock indicator unavailable: {error}.");
                }
            }

            var (state, summary) = WakeLockStatus.Compute(
                entries, (uint)Environment.ProcessId);
            viewModel.WakeLockSummary = summary;
            overlay.SetKeepAwakeStatus(state);
        }
        finally
        {
            _wakeLockQueryRunning = false;
        }
    }

    /// <summary>Feeds one canonical sample from the plugin into WSGM's own navigation.</summary>
    /// <param name="sample">The sample, already filtered for UI consumption by the manager.</param>
    /// <param name="held">The sample translated into WSGM's UI button vocabulary.</param>
    /// <remarks>
    ///     The manager decides what the UI may see and what still belongs to the game; this only routes
    ///     what it was given. The first sample is what makes the managed source healthy and completes
    ///     the switch away from SDL.
    /// </remarks>
    public void SubmitCanonicalSample(CanonicalControllerSample sample, GamepadButtons held)
    {
        _uiInput.Submit(sample, held);
    }

    /// <summary>Reports that controller management stopped delivering.</summary>
    /// <remarks>
    ///     SDL is still subscribed and running throughout, so this is a fall back to something already
    ///     live rather than a start — the UI cannot be left with no source.
    /// </remarks>
    public void ManagedInputLost()
    {
        _uiInput.ManagedSourceLost();
    }

    /// <summary>Claims this controller's Steam Input lease for a focus-taking surface.</summary>
    /// <remarks>
    ///     The lease blocks Steam's controller access only while SDL needs direct input for the sheet,
    ///     then lets Steam rediscover the controller after the last surface closes.
    /// </remarks>
    private void AcquireSteamInputLease()
    {
        _leaseReleased = false;
        // User opt-out: never touch Steam at all. The config watcher replaces
        // _config wholesale on reload, so a change is picked up without a restart —
        // but it is read HERE, at the top of an open, so it takes effect at the NEXT
        // surface open, not on the surface already on screen. A lease already applied
        // is deliberately NOT released when the opt-out arrives mid-surface: the
        // release hands the pad back to Steam's desktop profile, which per docs\steam-input.md
        // swallows it from SDL system-wide, so a controller user who turned this off
        // from the open Settings window would lose navigation on the very click that
        // saved it. The lease is scoped to the surface lifetime by specification
        // (docs\steam-input.md, Overlay\AGENTS.md): acquire before a surface opens,
        // release only after the last one closes. Controller input in a panel opened
        // with the opt-out active then depends on what Steam's desktop profile
        // leaves us.
        if (!_config.SteamInputLeaseEnabled)
        {
            Log.Info("Steam Input lease disabled in settings — surface opens without blocking Steam Input.");
            return;
        }

        // Deliberately NOT gated on SteamInputBlocker.IsApplied: the lease is
        // process-wide, so "applied" can just as well mean ANOTHER owner holds it
        // (the settings window this panel opened). Claiming it under our own name is
        // what stops that owner's release from leaving this surface unblocked —
        // docs\steam-input.md. AcquireFor is a no-op inside the blocker when the lease is
        // already live, so an inherited lease still costs no release/re-inject churn.
        if (_leaseAcquireTask is { IsCompleted: false })
        {
            return;
        }

        var pendingRelease = _leaseReleaseTask;
        _leaseAcquireTask = pendingRelease is { IsCompleted: false }
            ? pendingRelease.ContinueWith(_ => SteamInputBlocker.AcquireFor(_leaseOwner), TaskScheduler.Default)
            : Task.Run(() => SteamInputBlocker.AcquireFor(_leaseOwner));
    }

    /// <summary>
    ///     At most one release per lease acquisition from this controller.
    ///     Dispose releases early, so the deferred Closed handler cannot tear down a
    ///     replacement controller's live surface. The blocker only really lets go of
    ///     the lease when no other owner still claims it.
    /// </summary>
    private void ReleaseSteamInputLease(string reason = "surface-closed")
    {
        if (_leaseReleased)
        {
            return;
        }

        _leaseReleased = true;
        var pendingAcquire = _leaseAcquireTask;
        var owner = _leaseOwner;
        _leaseAcquireTask = null;
        _leaseReleaseTask = Task.Run(async () =>
        {
            if (pendingAcquire is not null)
            {
                try
                {
                    await pendingAcquire;
                }
                catch (Exception ex)
                {
                    // The acquire logs its own failures and does not throw; if one
                    // ever did, the release must still run — a swallowed release is
                    // a lease that outlives every surface; see docs\steam-input.md.
                    Log.Warn($"Steam Input lease acquire faulted before release ({owner}): {ex.Message}");
                }
            }

            SteamInputBlocker.ReleaseFor(owner, reason);
        });
    }

    /// <summary>
    ///     Picking an Open apps chip dismisses the sheet and brings the app
    ///     forward (Steam via the UIPI-proof protocol). The switched-to window must stay
    ///     foreground, so the sheet's focus restore is suppressed; see the focus-restore finding in
    ///     <c>docs\overlay-and-input.md</c>.
    /// </summary>
    private void PickWindow(AppSwitcherEntry entry)
    {
        if (_disposed || _overlay is not { } window)
        {
            return;
        }

        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        Log.Info($"Open apps: focusing '{entry.Title}'.");
        _suppressFocusRestore = true;
        Log.Observe(PickWindowAsync(entry, window, cancellation), "Open apps activation");
        CloseOverlay(true);
    }

    /// <summary>Hands the user from the overlay's Game Library to a page inside Steam.</summary>
    /// <param name="target">Which page.</param>
    /// <remarks>
    ///     The same order as picking a window: the page is asked for, the sheet closes, and only once
    ///     it has closed and the input lease is back does Steam get the focus. A bare dismissal
    ///     returns focus to whatever had it before, which is often not Steam.
    /// </remarks>
    private void OpenGameLibraryInSteam(GameLibrarySteamTarget target)
    {
        if (_disposed || _overlay is not { } window || OpenInSteam is not { } open)
        {
            return;
        }

        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        _suppressFocusRestore = true;
        var navigation = open(target, cancellation.Token);
        Log.Observe(OpenGameLibraryInSteamAsync(navigation, window, cancellation), "Game Library hand-off");
        CloseOverlay(true);
    }

    private async Task OpenGameLibraryInSteamAsync(Task<bool> navigation, OverlayWindow window,
        CancellationTokenSource cancellation)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += OnClosed;
        try
        {
            var navigated = await navigation.WaitAsync(cancellation.Token);
            await closed.Task.WaitAsync(cancellation.Token);
            if (_leaseReleaseTask is { } release)
            {
                await release.WaitAsync(cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            if (!navigated)
            {
                // Steam still gets the focus: the user asked to go there, and the page is one press
                // away from where they land.
                Log.Warn("Game Library: Steam did not take the requested page; focusing Steam as it is.");
            }

            _modes.FocusSteam();
        }
        catch (OperationCanceledException)
        {
        } // A reopened sheet or session shutdown ends the hand-off.
        finally
        {
            window.Closed -= OnClosed;
            if (ReferenceEquals(_windowReturnCancellation, cancellation))
            {
                _windowReturnCancellation = null;
            }

            cancellation.Dispose();
        }

        return;

        void OnClosed(object? sender, EventArgs args)
        {
            closed.TrySetResult();
        }
    }

    private async Task PickWindowAsync(AppSwitcherEntry entry, OverlayWindow window,
        CancellationTokenSource cancellation)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += OnClosed;
        try
        {
            await closed.Task.WaitAsync(cancellation.Token);
            if (_leaseReleaseTask is { } release)
            {
                await release.WaitAsync(cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            if (entry.IsSteam)
            {
                _modes.FocusSteam();
            }
            else if (GameReturn is { } gameReturn)
            {
                await gameReturn.ReturnAsync(entry.Hwnd, entry.ProcessId, cancellation.Token);
            }
            else
            {
                NativeMethods.GetWindowThreadProcessId(entry.Hwnd, out var pid);
                if (pid == entry.ProcessId)
                {
                    WindowFinder.BringToForeground(entry.Hwnd);
                }
            }
        }
        catch (OperationCanceledException)
        {
        } // A reopened sheet or session shutdown ends the return.
        finally
        {
            window.Closed -= OnClosed;
            if (ReferenceEquals(_windowReturnCancellation, cancellation))
            {
                _windowReturnCancellation = null;
            }

            cancellation.Dispose();
        }

        return;

        void OnClosed(object? sender, EventArgs args)
        {
            closed.TrySetResult();
        }
    }

    private static void StartTaskManager()
    {
        var taskmgr = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
        // ShellExecute-open: Taskmgr auto-elevates through its own manifest.
        if (!AppLauncher.Open(taskmgr).Started)
        {
            return;
        }

        Log.Info("Started Task Manager.");

        // It opens while our focused panel is closing, so the game underneath
        // reclaims the foreground and Task Manager lands behind it. Wait for
        // its window and promote it.
        FocusTaskManagerWhenVisible(1);
    }

    /// <summary>
    ///     Polls for the Task Manager window (12 tries, 300 ms apart) on the
    ///     UI thread and promotes it to the foreground once found.
    /// </summary>
    private static void FocusTaskManagerWhenVisible(int attempt)
    {
        RunOnUiThreadAfter(TimeSpan.FromMilliseconds(300), () =>
        {
            // Only the real System32 Task Manager qualifies — never promote a
            // same-named exe running from elsewhere to the foreground.
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
            var pids = WindowFinder.FindProcessIds("Taskmgr");
            pids.RemoveWhere(pid => !WindowFinder.ProcessImagePathEquals(pid, expected));
            var hwnd = WindowFinder.FindWindow(pids, null);
            if (hwnd != 0)
            {
                WindowFinder.BringToForeground(hwnd);
                return;
            }

            if (attempt >= 12)
            {
                Log.Warn("Task Manager window not found to focus.");
                return;
            }

            FocusTaskManagerWhenVisible(attempt + 1);
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

    private void ClaimUiSurface(string surfaceId)
    {
        if (_uiSurfaces.Add(surfaceId))
        {
            UiSurfaceOpened?.Invoke(surfaceId);
            return;
        }

        Log.Change(
            $"ui-surface.{surfaceId}",
            $"Managed UI capture claim skipped because {surfaceId} already owns it.");
    }

    private void ReleaseUiSurface(string surfaceId)
    {
        if (_uiSurfaces.Remove(surfaceId))
        {
            UiSurfaceClosed?.Invoke(surfaceId);
            return;
        }

        Log.Change(
            $"ui-surface.{surfaceId}",
            $"Managed UI capture release skipped because {surfaceId} has no claim.");
    }

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
        // A trim mid-open would just soft-fault everything straight back.
        _pendingTrim?.Dispose();
        _pendingTrim = null;
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
        var explorerRunning = ExplorerControl.IsRunningInSession();
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
        _overlay.AttachSteamOwnership(SteamOwnership);
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
        _overlay.AttachDevicePrerequisites(_sources.DevicePrerequisites);
        _overlay.AttachCommonPlugins(_sources.CommonPlugins);
        _overlay.AttachGameLibrary(_sources.GameLibrary);
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
        _navigation = new GamepadNavigation(_uiInput, _overlay, OnOverlayBack,
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
            true);
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
        if (_closePending)
        {
            // Re-summoned inside the 150 ms deferred close: cancel the pending
            // Close() and keep the window — otherwise the timer would destroy
            // the just-reactivated panel and release its lease under it.
            _pendingClose?.Dispose();
            _pendingClose = null;
            _closePending = false;
            // The action that requested the close was abandoned with it: a
            // handoff that never happens must not make the eventual close skip
            // the lease release (a lease with no surface on screen), and a
            // suppressed focus restore must not stay latched for the rest of
            // this panel's life; see the focus-restore finding in docs\overlay-and-input.md.
            _handoffLease = false;
            _settingsHandoffWindow?.CompleteSteamInputLeaseHandoff();
            _settingsHandoffWindow = null;
            _suppressFocusRestore = false;
            Log.Info("Overlay re-shown during deferred close — pending close cancelled.");
        }

        if (_overlayViewModel is not null)
        {
            _overlayViewModel.WarningText = _pendingWarning;
            // Recompute what the fresh-open path computes — Steam may have died
            // or the desktop may have changed while the panel stayed open.
            _overlayViewModel.ExplorerRunning = ExplorerControl.IsRunningInSession();
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

        overlay.Activate();
    }

    /// <summary>Connects the sheet's requests to the session actions they start.</summary>
    /// <param name="overlay">The sheet being opened.</param>
    /// <param name="vm">Its view model, which some requests update.</param>
    private void WireOverlayRequests(OverlayWindow overlay, OverlayViewModel vm)
    {
        overlay.GameLibraryOpenInSteamRequested += OpenGameLibraryInSteam;
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

            var explorerRunning = ExplorerControl.IsRunningInSession();
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
            // block input; a failed read still refuses to write blind.
            // Enqueue on the UI thread, before yielding, to preserve explicit choice order
            // across rapid selections and close/reopen. A failed predecessor is not retried.
            var write = _powerTimeoutWrite.ContinueWith(_ =>
            {
                // The session's owner skips display presets Steam's screensaver forbids and tells
                // Steam's Screensaver settings about the change.
                if (_displayTimeouts is not null)
                {
                    _displayTimeouts.Select(kind, seconds);
                    return;
                }

                lock (PowerSchemes.MutationGate)
                {
                    var current = PowerTimeouts.Read(kind);
                    if (current is not null)
                    {
                        PowerTimeouts.Write(kind, seconds);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _powerTimeoutWrite = write;
            try
            {
                await write;
            }
            catch (Exception ex)
            {
                Log.Error("The selected power timeout could not be applied", ex);
            }

            if (ReferenceEquals(_overlayViewModel, vm))
            {
                RefreshPowerTimeouts(vm);
            }
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
            // Hand the lease to Settings instead of releasing it: the close below
            // keeps Steam's controller blocked continuously, so Settings inherits a
            // live lease with no release/re-inject churn.
            var settings = new SettingsWindow(true);
            _settingsHandoffWindow = settings;
            _handoffLease = true;
            ClaimUiSurface(SettingsSurface);
            settings.Closed += (_, _) => ReleaseUiSurface(SettingsSurface);
            CloseOverlay();
            // A shell session normally has no main window. Opening settings in this
            // process keeps quick access responsive and avoids starting a second shell.
            // gameModeSurface: the window takes over as the on-screen surface and owns
            // the handed-off Steam Input lease, else Steam's desktop profile grabs the
            // pad over Settings.
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
        _closePending = false;
        _pendingClose?.Dispose();
        _pendingClose = null;
        // Detach surfaces before disposing the live managers they observe.
        _overlay?.CloseAllSurfaces();
        _pendingTopmostRestore?.Dispose();
        _pendingTopmostRestore = null;
        StopSwitcherRefresh();
        _switcherViewModel = null;
        _systemStatus?.Dispose();
        _systemStatus = null;
        // Free the rasterized icons with the sheet; the next open re-resolves.
        _iconCache?.Clear();
        // Same for the cached Steam pid set: the next session starts fresh.
        _steamPidsAtUtc = default;
        // Give Steam its pad back the moment the sheet is gone. A Settings
        // handoff ends only this overlay's named claim after Settings has
        // registered its own, which keeps the shared native lease continuous.
        if (_handoffLease)
        {
            _handoffLease = false;
            var settings = _settingsHandoffWindow;
            _settingsHandoffWindow = null;
            Log.Info("Steam Input lease handed off to the settings window.");
            // Settings registered its own owner in Opened before this deferred
            // close completes. End the overlay's claim now; abandoning it here
            // leaves a phantom overlay owner until the panel is opened and
            // closed again. If Steam was unavailable, there is no live native
            // lease to churn and Settings' worker still owns its claim.
            ReleaseSteamInputLease("handed-off-to-settings");
            // The overlay itself can momentarily deactivate the new Settings
            // window during this required 150 ms close. Only now should normal
            // focus-based lease release resume.
            settings?.CompleteSteamInputLeaseHandoff();
        }
        else
        {
            ReleaseSteamInputLease();
        }

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
        if (!_suppressFocusRestore && _restoreFocusTo != 0 && !ExplorerControl.IsRunningInSession())
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
        else
        {
            // The shell goes invisible again — give the freed UI memory back
            // once the close (and any focus restore) has settled.
            _pendingTrim?.Dispose();
            _pendingTrim = RunOnUiThreadAfter(TimeSpan.FromSeconds(5),
                () => MemoryTrim.TrimBestEffort("overlay closed"));
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
    ///     The bottom-swipe entry: the sheet, with controller focus landing on
    ///     the Open apps strip rather than the selected root's first row — one gesture to
    ///     the running programs, which is what the bottom edge used to open.
    /// </summary>
    public void ShowOverlayOnOpenApps()
    {
        ShowOverlay();
        // Background priority: after the window's own Opened focus (DefaultFocusTarget)
        // AND the first layout pass, which is what realizes the chip buttons.
        Dispatcher.UIThread.Post(
            () => _overlay?.FocusOpenApps(), DispatcherPriority.Background);
    }

    /// <summary>
    ///     Opens the sheet on its Open apps strip, or closes it when it is up — the
    ///     OEM button action that used to toggle the taskbar.
    /// </summary>
    public void ToggleOpenApps()
    {
        if (_overlay is null)
        {
            ShowOverlayOnOpenApps();
        }
        else
        {
            CloseOverlay();
        }
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

    private bool OpenKeyboard(string prompt, string initial, int maxLength, Action<string> onAccept)
    {
        if (_overlay is not { } overlay)
        {
            return false;
        }

        var keyboard = new KeyboardPanel(prompt, initial, maxLength);
        keyboard.Accepted += onAccept;
        overlay.ShowKeyboardSurface(keyboard);
        return true;
    }

    private void CloseKeyboardNow()
    {
        _overlay?.CloseAllSurfaces();
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
    ///     Opens the power menu on the current overlay, or from the desktop without a Steam lease.
    ///     Hardware-button capture is owned by the session's input integration.
    /// </summary>
    public void ShowPowerMenu()
    {
        if (_disposed)
        {
            return;
        }

        var standalone = _overlay is null;
        ShowOverlayCore(!ExplorerControl.IsRunningInSession());
        _powerMenuOnly |= standalone;
        _overlay?.ShowPowerMenu();
    }

    /// <summary>Handles a repeated power-menu request as cancellation.</summary>
    public void TogglePowerMenu()
    {
        if (PowerMenuOpen)
        {
            _overlay?.CloseActiveSurface();
            return;
        }

        ShowPowerMenu();
    }

    /// <summary>
    ///     Consumes a short power press while the menu is open. Callers must not perform their
    ///     normal sleep action when this returns true. This does not install hardware capture.
    /// </summary>
    public bool TryConsumePowerPress()
    {
        if (!PowerMenuOpen)
        {
            return false;
        }

        _overlay?.CloseActiveSurface();
        return true;
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
        return UiScale(ExplorerControl.IsRunningInSession());
    }

    private double UiScale(bool explorerRunning)
    {
        return explorerRunning
            ? 1.0
            : DisplayScale.GetUiScalePercent(_config) / 100.0;
    }

    /// <summary>
    ///     Attaches (or detaches, with null) the game-mode tray host whose
    ///     icons render in the sheet's bottom rail. ShellSession owns the host's
    ///     lifecycle — created per game-mode span, destroyed before explorer starts.
    /// </summary>
    /// <param name="host">The live tray host, or null when leaving game mode.</param>
    public void AttachTrayHost(TrayHost? host)
    {
        if (_trayHost is not null)
        {
            _trayHost.IconsChanged -= OnTrayIconsChanged;
        }

        _trayHost = host;
        if (host is not null)
        {
            host.IconsChanged += OnTrayIconsChanged;
        }

        OnTrayIconsChanged();
    }

    private void OnTrayIconsChanged()
    {
        _switcherViewModel?.ReconcileTray(_trayHost?.Table.Icons ?? []);
    }

    /// <summary>
    ///     Queues an off-thread snapshot of the process/window tables, then reconciles the
    ///     Open apps chips on Avalonia's dispatcher. While the sheet is open the highlight uses the
    ///     captured pre-open foreground window.
    /// </summary>
    private void RefreshSwitcherEntries()
    {
        var viewModel = _switcherViewModel;
        if (viewModel is null || Interlocked.CompareExchange(ref _switcherRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var refreshSteamPids = _steamPidsAtUtc == default
                               || now - _steamPidsAtUtc >= TimeSpan.FromSeconds(5);
        HashSet<uint> cachedSteamPids = [.. _steamPids];
        var active = _overlay is { IsVisible: true }
            ? _restoreFocusTo
            : NativeMethods.GetForegroundWindow();
        Log.Observe(
            RefreshSwitcherEntriesAsync(
                viewModel,
                active,
                refreshSteamPids,
                cachedSteamPids,
                now),
            "Open apps refresh");
    }

    private async Task RefreshSwitcherEntriesAsync(
        AppSwitcherViewModel viewModel,
        nint active,
        bool refreshSteamPids,
        HashSet<uint> cachedSteamPids,
        DateTime requestedAtUtc)
    {
        try
        {
            // EnumWindows, DWM queries and the process-table snapshot are synchronous Win32 work.
            // Avalonia's dispatcher also owns pointer delivery and the 16 ms gamepad poll, so only
            // a detached snapshot returns to it.
            (HashSet<uint> SteamPids, IReadOnlyList<WindowFinder.AppWindow> Windows) snapshot =
                await Task.Run(() =>
                {
                    var steamPids = refreshSteamPids
                        ? WindowFinder.FindProcessIds(Steam.ProcessNames)
                        : cachedSteamPids;
                    return (steamPids, WindowFinder.ListSwitchableWindows());
                }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(_switcherViewModel, viewModel))
                {
                    return;
                }

                if (refreshSteamPids)
                {
                    _steamPids = snapshot.SteamPids;
                    _steamPidsAtUtc = requestedAtUtc;
                }

                viewModel.Reconcile(
                    snapshot.Windows,
                    active,
                    window => CreateSwitcherEntry(window, snapshot.SteamPids));
            }, DispatcherPriority.Background);
        }
        finally
        {
            Interlocked.Exchange(ref _switcherRefreshInFlight, 0);
        }
    }

    private AppSwitcherEntry CreateSwitcherEntry(
        WindowFinder.AppWindow window,
        HashSet<uint> steamPids)
    {
        // Cached icons are handed over synchronously; a miss resolves off the UI thread
        // (cross-process WM_GETICON probes plus a possible exe read) and lands in place.
        Bitmap? icon = null;
        if (_iconCache is not null && !_iconCache.TryGetCached(window.Hwnd, out icon))
        {
            _iconCache.ResolveInBackground(window.Hwnd, window.ProcessId, ApplyResolvedIcon);
        }

        return new AppSwitcherEntry(
                window.Hwnd,
                window.Title,
                steamPids.Contains(window.ProcessId),
                icon)
            { ProcessId = window.ProcessId };
    }

    /// <summary>
    ///     Places a background-resolved icon on its chip, if that chip is still on
    ///     the open sheet. Runs off the UI thread, so it marshals before touching view state.
    /// </summary>
    private void ApplyResolvedIcon(nint hwnd, Bitmap? icon)
    {
        if (icon is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // The window may have closed, or the sheet may have been dismissed and its
            // cache cleared, between the resolve starting and finishing.
            if (_switcherViewModel is null || _overlay is not { IsVisible: true })
            {
                return;
            }

            foreach (var entry in _switcherViewModel.Entries)
            {
                if (entry.Hwnd != hwnd)
                {
                    continue;
                }

                entry.Icon = icon;
                return;
            }
        });
    }

    /// <summary>
    ///     Keeps the open sheet's strip current (new/closed windows, titles,
    ///     minimize state) without disturbing the focused chip — Reconcile updates in place.
    /// </summary>
    private void StartSwitcherRefresh()
    {
        StopSwitcherRefresh();
        // Parameterless ctor + explicit Start; see the DispatcherTimer finding in
        // docs\overlay-and-input.md.
        _switcherRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _switcherRefresh.Tick += (_, _) => RefreshSwitcherEntries();
        _switcherRefresh.Start();
    }

    private void StopSwitcherRefresh()
    {
        _switcherRefresh?.Stop();
        _switcherRefresh = null;
    }

    /// <summary>
    ///     Forwards a tray-icon activation to its owner. For context menus
    ///     the sheet additionally drops Topmost for a while: WinForms tray menus shown
    ///     via plain Show() (Handheld Companion since its commit c86932bc) are
    ///     NON-topmost and never activated — over a topmost sheet they open BEHIND it,
    ///     which reads as "the menu doesn't appear" (device-reported).
    /// </summary>
    private void OnTrayIconActivated(TrayIconEntry entry, bool contextMenu, PixelPoint anchor)
    {
        if (contextMenu)
        {
            if (_overlay is not null)
            {
                _overlay.Topmost = false;
                _pendingTopmostRestore?.Dispose();
                _pendingTopmostRestore = RunOnUiThreadAfter(TimeSpan.FromSeconds(10), () =>
                {
                    _pendingTopmostRestore = null;
                    _overlay?.Topmost = true;
                });
            }
        }
        else
        {
            // A plain activation opens/shows the owning app — dismiss the sheet so it
            // comes forward (same rule as picking an Open apps chip). A context-menu
            // request keeps the sheet: the menu pops over it.
            _suppressFocusRestore = true;
            CloseOverlay();
        }

        _trayHost?.SendClick(entry.Icon, contextMenu, anchor.X, anchor.Y);
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
        if (_overlay is null || _closePending)
        {
            return;
        }

        // Deferred: a touch tap's DefWindowProc promotion delivers a synthesized
        // mouse click AFTER this dispatch. If the window were already destroyed,
        // that click would land on whatever sits underneath (user-reproduced).
        // Kept open a beat, the window's own hook eats the synthesized click.
        // ShowOverlay cancels this via _pendingClose when re-summoned in time.
        _closePending = true;
        _overlay.IsEnabled = false;
        _navigation?.IsEnabled = false;
        _pendingClose = RunOnUiThreadAfter(TouchInput.CloseGrace, () =>
        {
            _closePending = false;
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

    private void HideTouchEdges()
    {
        _touchSwipes?.Disarm();
    }

    private void ShowTouchEdges()
    {
        _touchSwipes?.Arm();
    }

    private void DisposeTouchEdges()
    {
        if (_touchSwipes is null)
        {
            return;
        }

        _touchSwipes.Triggered -= OnSwipeTriggered;
        _touchSwipes.Dispose();
        _touchSwipes = null;
    }
}
