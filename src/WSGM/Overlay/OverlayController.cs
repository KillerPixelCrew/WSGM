using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Owns the overlay activation surfaces (hotkey, raw-input touch swipes) and the
/// two focus-taking WSGM surfaces themselves: the quick-access overlay
/// (ShowOverlay) and the game-mode taskbar (ShowTaskbar). One controller owns both
/// because they share every piece of invariant-critical state — the Steam Input
/// lease, the touch-swipe disarm/re-arm cycle, tap-outside dismissal, the gamepad
/// service, and the focus-restore discipline — and the two surfaces are mutually
/// exclusive (opening one closes the other).</summary>
public sealed class OverlayController : IDisposable
{
    private const string QuickAccessSurface = "quick-access";
    private const string TaskbarSurface = "taskbar";
    private const string SettingsSurface = "settings";
    private readonly HashSet<string> _uiSurfaces = new(StringComparer.Ordinal);
    private AppConfig _config;
    private readonly SteamMonitor? _monitor;
    private readonly SessionModes _modes;
    private readonly KeepAwakeService? _keepAwake;
    private readonly IDeviceOverlaySource? _device;
    private readonly IPerformanceOverlaySource? _performance;

    /// <summary>
    /// The session's audio manager, shared with the taskbar's status cluster rather than owned.
    /// </summary>
    /// <remarks>
    /// Null in overlay-test, where no session owns one and the cluster creates its own.
    /// </remarks>
    private readonly AudioManager? _sessionAudio;

    /// <summary>
    /// The session's radio manager, shared with the taskbar's status cluster rather than owned.
    /// </summary>
    /// <remarks>
    /// Null in overlay-test, where no session owns one and the cluster creates its own.
    /// </remarks>
    private readonly RadioManager? _sessionRadios;
    private readonly HotkeyService _hotkey;
    private readonly GamepadService _gamepad = new();

    /// <summary>What every navigation surface here subscribes to.</summary>
    /// <remarks>
    /// The surfaces take the router rather than <see cref="GamepadService"/> so they see whichever
    /// source is delivering. With controller management off this is SDL, exactly as before. With it
    /// on, WSGM's own UI can finally be driven by the controls SDL cannot see on a handheld — the
    /// rear paddles, Quick Access, and the trackpad clicks.
    /// <para>
    /// The chord watcher deliberately stays on the raw SDL service. The chord is what opens the
    /// overlay, so it has to keep working when the managed source is not running, and it is the one
    /// thing that must not change behaviour with the source.
    /// </para>
    /// </remarks>
    private readonly UiInputRouter _uiInput;
    private readonly GamepadChordWatcher _chordWatcher;
    private TouchSwipeMonitor? _touchSwipes;

    /// <summary>What tap watching and gamepad navigation were set to when a modal
    /// system dialog took the screen, so closing it restores exactly that.</summary>
    private bool _dialogPriorWatchTaps;
    private bool _dialogPriorNavigation;
    private OverlayWindow? _overlay;
    private OverlayViewModel? _overlayViewModel;
    private GamepadNavigation? _navigation;
    private TaskbarWindow? _taskbar;
    private TaskbarViewModel? _taskbarViewModel;
    private GamepadNavigation? _taskbarNavigation;
    private SystemStatus? _systemStatus;
    private WindowIconCache? _iconCache;
    private Avalonia.Threading.DispatcherTimer? _taskbarRefresh;
    private TrayHost? _trayHost;
    private string _pendingWarning = "";
    private bool _reopenOverlayForWarning;
    private bool _disposed;
    private readonly bool _previewOnly;

    /// <summary>Creates the overlay controller and its input activation surfaces.</summary>
    /// <param name="config">The initial shell configuration.</param>
    /// <param name="monitor">The optional Steam lifecycle monitor shared by the shell.</param>
    /// <param name="modes">The session-mode coordinator that performs requested transitions.</param>
    /// <param name="keepAwake">The optional session keep-awake service behind the Power
    /// tab's toggle; null (the Settings preview overlay) hides the row.</param>
    /// <param name="previewOnly">True for a surface that only demonstrates layout and
    /// input — Settings' "Test panel"/"Test taskbar" and <c>--overlay-test</c>. It hides
    /// the desktop/game-mode row and refuses the transition even if it is reached, because
    /// those processes have no ShellSession, tray host or crash-loop/watchdog recovery:
    /// one press would exit Explorer and strand the user with no shell.</param>
    public OverlayController(AppConfig config, SteamMonitor? monitor, SessionModes modes,
        KeepAwakeService? keepAwake = null, bool previewOnly = false)
        : this(config, monitor, modes, keepAwake, previewOnly, device: null)
    {
    }

    internal OverlayController(AppConfig config, SteamMonitor? monitor, SessionModes modes,
        KeepAwakeService? keepAwake, bool previewOnly, IDeviceOverlaySource? device,
        IPerformanceOverlaySource? performance = null,
        AudioManager? audio = null,
        RadioManager? radios = null)
    {
        _sessionAudio = audio;
        _sessionRadios = radios;
        _config = config;
        _monitor = monitor;
        _modes = modes;
        _keepAwake = keepAwake;
        _device = device;
        _performance = performance;
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

    /// <summary>Applies changed gesture settings without replacing the monitor.</summary>
    /// <param name="gestures">The new edge-swipe configuration.</param>
    public void ApplyGestures(GestureConfig gestures)
    {
        // The monitor stays alive even with both edges disabled: tap-outside
        // dismissal of the overlay rides on the same raw-input observer.
        if (_touchSwipes is null)
        {
            _touchSwipes = new TouchSwipeMonitor();
            _touchSwipes.Triggered += OnSwipeTriggered;
            _touchSwipes.TappedAt += OnTappedAt;
        }
        _touchSwipes.Configure(gestures);

        // Both surfaces disarm the edges: with the full-width bar docked on the
        // bottom edge, a re-arm would read touches inside the bar as bottom-edge
        // swipes (every other site pairs the disarm with both surfaces).
        if (_overlay is not null || _taskbar is not null)
        {
            HideTouchEdges();
        }
        else
        {
            ShowTouchEdges();
        }
    }

    /// <summary>What an edge swipe opens (routing result).</summary>
    public enum SwipeAction
    {
        /// <summary>The swipe is ignored.</summary>
        None,

        /// <summary>The quick-access panel opens.</summary>
        QuickAccess,

        /// <summary>The game-mode taskbar opens.</summary>
        Taskbar,

        /// <summary>Steam Big Picture's left-side Steam menu opens.</summary>
        SteamMenu,

        /// <summary>Steam Big Picture's right-side Quick Access Menu opens.</summary>
        SteamQuickAccess,
    }

    private void OnSwipeTriggered(ScreenEdge edge)
    {
        switch (DecideSwipe(edge, _config.Gestures.BottomEdgeAction, ExplorerControl.IsRunningInSession()))
        {
            case SwipeAction.Taskbar:
                ShowTaskbar();
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
            default:
                Log.Info("Bottom swipe ignored in desktop mode (explorer's taskbar owns the edge).");
                break;
        }
    }

    /// <summary>The pure edge-routing decision: left/top open Steam's own menus,
    /// right always opens WSGM quick access, and a bottom edge assigned to the
    /// taskbar opens it in game mode but is IGNORED in desktop mode — explorer's
    /// real taskbar owns that edge there, and falling back to the panel read as a
    /// regression (device-reported).</summary>
    /// <param name="edge">The swiped screen edge.</param>
    /// <param name="bottomEdgeAction">The configured bottom-edge action.</param>
    /// <param name="explorerRunning">Whether the session currently has a desktop.</param>
    /// <returns>What the swipe opens, if anything.</returns>
    public static SwipeAction DecideSwipe(ScreenEdge edge, EdgeAction bottomEdgeAction, bool explorerRunning)
    {
        if (edge == ScreenEdge.Left)
        {
            return SwipeAction.SteamMenu;
        }
        if (edge == ScreenEdge.Top)
        {
            return SwipeAction.SteamQuickAccess;
        }
        if (edge == ScreenEdge.Right || bottomEdgeAction == EdgeAction.QuickAccess)
        {
            return SwipeAction.QuickAccess;
        }
        return explorerRunning ? SwipeAction.None : SwipeAction.Taskbar;
    }

    /// <summary>Sets a non-fatal warning to show the next time the overlay opens.</summary>
    /// <param name="warning">The user-facing warning text, or an empty string to clear it.</param>
    public void SetWarning(string warning)
    {
        _pendingWarning = warning;
        if (_overlayViewModel is not null)
        {
            _overlayViewModel.WarningText = warning;
        }
    }

    private Shell.SdFormatManager? _formatManager;

    /// <summary>The shared removable-storage format manager backing the Tools
    /// tab's Format SD Card / Add Steam Library flow. Created on first use and
    /// kept for the controller's lifetime so a format survives the overlay
    /// closing; a completion reached while the overlay is closed surfaces
    /// through the warning bar on the next open.</summary>
    private Shell.SdFormatManager FormatManager
    {
        get
        {
            if (_formatManager is null)
            {
                _formatManager = new Shell.SdFormatManager();
                _formatManager.Finished += OnFormatFinished;
            }
            return _formatManager;
        }
    }

    private void OnFormatFinished(string message, bool success)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // While the overlay is open the sub-view already shows the outcome.
            // If it was closed mid-format (the run outlives the window), reopen
            // it to surface the result through the warning bar.
            if (_overlay is null && !_disposed)
            {
                SetWarning(message);
                ShowOverlay();
            }
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
    /// <param name="config">The freshly loaded configuration; it replaces the previous
    /// instance wholesale, so runtime state must stay on the controllers rather than
    /// on the configuration object.</param>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        // The master CEF switch is owned by ShellSession, which retracts injected UI
        // before closing it — setting it here as well would cut that retraction off.
        // UI-thread only: this writes view-model state, control titles and the
        // gamepad's DispatcherTimer with no marshalling of its own. ShellSession's
        // debounced config watcher already posts it; the Post below only keeps the
        // accent re-apply safe for this public entry point.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Themes.AccentPalette.Apply(Avalonia.Application.Current!, Themes.AccentPalette.Parse(config.AccentColor)));
        _modes.ApplyConfig(config);
        _hotkey.Apply(config.Hotkey);
        _chordWatcher.ApplyConfig(config.GamepadChord);
        var chordActive = config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0;
        if (chordActive && !_gamepad.IsRunning)
        {
            _gamepad.Start();
        }
        else if (!chordActive && _overlay is null && _gamepad.IsRunning)
        {
            _gamepad.Stop();
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
        }
        if (_overlay is not null || _taskbar is not null)
        {
            AcquireSteamInputLease();
        }
        Log.Info("Config reloaded.");
    }

    private void OnSteamExited()
    {
        if (_config.SteamAutoRelaunch)
        {
            Log.Info("Steam exited — auto-relaunching in 10 s.");
            RunOnUiThreadAfter(TimeSpan.FromMilliseconds(10_000), () =>
            {
                // Re-checked at fire time: a config reload (_config is replaced
                // wholesale) may have turned auto-relaunch off, or this
                // controller may have been disposed while the delay ran.
                if (_disposed || !_config.SteamAutoRelaunch)
                {
                    Log.Info("Auto-relaunch skipped: disabled or disposed meanwhile.");
                    return;
                }
                // The user may have switched to desktop mode (or closed Steam
                // deliberately) while this delay was in flight.
                if (_monitor?.Paused == true)
                {
                    Log.Info("Auto-relaunch skipped: monitor paused meanwhile.");
                    return;
                }
                _modes.StartOrFocusSteam();
            });
            return;
        }
        ShowOverlay();
    }

    private void OnSteamInputRecoveryWarning(string warning)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                SetWarning(warning);
            }
        });
    }

    /// <summary>Hides each CEF feature's sidebar button when that feature is off, so
    /// a disabled integration has no entry point. With CEF off entirely the
    /// launch-wrapper buttons fall back to copying the command instead of
    /// disappearing, which keeps them useful.</summary>
    /// <param name="vm">The panel's view model.</param>
    /// <param name="config">The configuration to read the gates from.</param>
    private static void ApplyCefVisibility(OverlayViewModel vm, AppConfig config)
    {
        vm.ShowLibraryTabs = config.Cef.Enabled && config.Cef.LibraryTabs;
        vm.ShowCardManager = config.Cef.Enabled && config.Cef.CardManager;
        vm.ShowArtwork = config.Cef.Enabled && config.Cef.Artwork;
        vm.ShowSdCard = config.Cef.Enabled && config.Cef.SdFormat;
        vm.ConfigureLaunchOptionsLive = config.Cef.Enabled;
        vm.InputLeaseUsesShim = config.SteamInputManagementEnabled;
    }

    /// <summary>Reads the four idle timeouts from the active power scheme into the
    /// Power tab's badges ("—" when the power API gives no answer).</summary>
    private static void RefreshPowerTimeouts(OverlayViewModel vm)
    {
        static string Format(int? seconds)
            => seconds is null ? "—" : PowerTimeouts.Describe(seconds.Value);
        vm.DisplayDcTimeout = Format(PowerTimeouts.Read(PowerTimeoutKind.DisplayDc));
        vm.DisplayAcTimeout = Format(PowerTimeouts.Read(PowerTimeoutKind.DisplayAc));
        vm.SleepDcTimeout = Format(PowerTimeouts.Read(PowerTimeoutKind.SleepDc));
        vm.SleepAcTimeout = Format(PowerTimeouts.Read(PowerTimeoutKind.SleepAc));
    }

    /// <summary>Mirrors keep-awake hold changes (poll loop or toggle, any thread)
    /// into an open panel's view model.</summary>
    private void OnKeepAwakeStateChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _overlayViewModel is null || _keepAwake is null)
            {
                return;
            }
            _overlayViewModel.KeepAwakeManualMode = _keepAwake.ManualMode;
            _overlayViewModel.KeepAwakeDownloadActive = _keepAwake.DownloadHold;
        });

    private Avalonia.Threading.DispatcherTimer? _wakeLockRefresh;
    private string? _lastWakeLockError;

    /// <summary>Polls the system-wide power-request list into the Keep Awake row's
    /// WakeWatch-style dot while the panel is open (~65 µs syscall, WakeWatch runs
    /// it at 1 Hz permanently). Started per ShowOverlay, stopped with the panel.</summary>
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
            _wakeLockRefresh = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };
            _wakeLockRefresh.Tick += (_, _) => RefreshWakeLockIndicator();
        }
        _wakeLockRefresh.Start();
    }

    private void StopWakeLockRefresh() => _wakeLockRefresh?.Stop();

    private void RefreshWakeLockIndicator()
    {
        if (_disposed || _overlay is null || _overlayViewModel is null || _keepAwake is null)
        {
            return;
        }
        var (entries, error) = Interop.PowerRequestList.Query();
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
        _overlayViewModel.WakeLockSummary = summary;
        _overlay.SetKeepAwakeStatus(state);
    }

    private bool _leaseReleased;
    private Task? _leaseAcquireTask;
    private Task? _leaseReleaseTask;
    private static int _nextLeaseOwnerId;

    /// <summary>This controller's identity in the blocker's process-wide ownership
    /// set. Per instance on purpose: a replacement controller (the Settings preview's
    /// "Test panel" pressed twice) claims the lease under its own name, so the
    /// outgoing controller's release cannot drop the live surface's lease.</summary>
    private readonly string _leaseOwner =
        $"overlay-controller#{System.Threading.Interlocked.Increment(ref _nextLeaseOwnerId)}";

    /// <summary>Feeds one canonical sample from the plugin into WSGM's own navigation.</summary>
    /// <param name="sample">The sample, already filtered for UI consumption by the manager.</param>
    /// <remarks>
    /// The manager decides what the UI may see and what still belongs to the game; this only routes
    /// what it was given. The first sample is what makes the managed source healthy and completes
    /// the switch away from SDL.
    /// </remarks>
    public void SubmitCanonicalSample(CanonicalControllerSample sample) =>
        _uiInput.Submit(sample);

    /// <summary>Reports that controller management stopped delivering.</summary>
    /// <remarks>
    /// SDL is still subscribed and running throughout, so this is a fall back to something already
    /// live rather than a start — the UI cannot be left with no source.
    /// </remarks>
    public void ManagedInputLost() => _uiInput.ManagedSourceLost();

    /// <summary>Claims this controller's Steam Input lease for a focus-taking surface.</summary>
    /// <remarks>
    /// The lease blocks Steam's controller access only while SDL needs direct input for the overlay
    /// or taskbar, then lets Steam rediscover the controller after the last surface closes.
    /// </remarks>
    private void AcquireSteamInputLease()
    {
        _leaseReleased = false;
        // User opt-out: never touch Steam at all. The config watcher replaces
        // _config wholesale on reload, so a change is picked up without a restart —
        // but it is read HERE, at the top of an open, so it takes effect at the NEXT
        // surface open, not on the surface already on screen. A lease already applied
        // is deliberately NOT released when the opt-out arrives mid-surface: the
        // release hands the pad back to Steam's desktop profile, which per invariant 1
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
        // invariant 1. AcquireFor is a no-op inside the blocker when the lease is
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

    /// <summary>At most one release per lease acquisition from this controller.
    /// Dispose releases early, so the deferred Closed handler cannot tear down a
    /// replacement controller's live surface. The blocker only really lets go of
    /// the lease when no other owner still claims it.</summary>
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
                    // a lease that outlives every surface (invariant 1).
                    Log.Warn($"Steam Input lease acquire faulted before release ({owner}): {ex.Message}");
                }
            }
            SteamInputBlocker.ReleaseFor(owner, reason);
        });
    }

    /// <summary>Picking a taskbar tile dismisses the bar and brings the app forward
    /// (Steam via the UIPI-proof protocol). The switched-to window must stay
    /// foreground, so the bar's focus restore is suppressed (invariant 6).</summary>
    private void PickTaskbarWindow(TaskbarEntry entry)
    {
        Log.Info($"Taskbar: focusing '{entry.Title}'.");
        _taskbarSuppressFocusRestore = true;
        CloseTaskbar();
        if (entry.IsSteam)
        {
            _modes.FocusSteam();
        }
        else
        {
            WindowFinder.BringToForeground(entry.Hwnd);
        }
    }

    private static void StartTaskManager()
    {
        var taskmgr = System.IO.Path.Combine(
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
        FocusTaskManagerWhenVisible(attempt: 1);
    }

    /// <summary>Polls for the Task Manager window (12 tries, 300 ms apart) on the
    /// UI thread and promotes it to the foreground once found.</summary>
    private static void FocusTaskManagerWhenVisible(int attempt)
    {
        RunOnUiThreadAfter(TimeSpan.FromMilliseconds(300), () =>
        {
            // Only the real System32 Task Manager qualifies — never promote a
            // same-named exe running from elsewhere to the foreground.
            var expected = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
            var pids = WindowFinder.FindProcessIds("Taskmgr");
            pids.RemoveWhere(pid => !WindowFinder.ProcessImagePathEquals(pid, expected));
            var hwnd = WindowFinder.FindWindow(pids, windowClass: null);
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

    /// <summary>Window focused when the overlay opened. Exclusive-fullscreen games
    /// minimize the moment our panel takes focus — closing the panel calls them
    /// back (restore + foreground), unless an overlay action redirected focus.</summary>
    private nint _restoreFocusTo;
    private bool _suppressFocusRestore;

    /// <summary>Set for the one overlay close that opens the settings window: the
    /// lease is handed to Settings rather than released, so Steam's controller is
    /// not dropped and re-revoked across the switch.</summary>
    private bool _handoffLease;
    private SettingsWindow? _settingsHandoffWindow;

    /// <summary>Raised whenever quick access comes up (hotkey, swipe, chord,
    /// Steam-exit pop, warning reopen). The boot splash dismisses on it — the
    /// panel always outranks the splash.</summary>
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
        if (_disposed)
        {
            return;
        }
        OverlayShown?.Invoke();
        // A trim mid-open would just soft-fault everything straight back.
        _pendingTrim?.Dispose();
        _pendingTrim = null;
        // Surface switch: the bar yields and the panel inherits its restore
        // target — GetForegroundWindow() would report the still-open bar, not the
        // game the user actually came from.
        nint inheritedRestore = 0;
        if (_taskbar is not null)
        {
            inheritedRestore = _taskbarRestoreFocusTo;
            _taskbarSuppressFocusRestore = true;
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = false;
            }
            CloseTaskbar();
        }
        if (_overlay is null)
        {
            _restoreFocusTo = inheritedRestore != 0 ? inheritedRestore : Interop.NativeMethods.GetForegroundWindow();
            _suppressFocusRestore = false;
        }
        AcquireSteamInputLease();
        HideTouchEdges();
        if (_overlay is not null)
        {
            if (_keyboardWindow is not null)
            {
                _keyboardNavigation?.Dispose();
                _keyboardNavigation = null;
                _keyboardWindow.Close();
                _keyboardWindow = null;
                _overlay.KeyboardOwnsFocus = false;
            }
            if (_navigation is not null)
            {
                _navigation.IsEnabled = true;
            }
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
                // this panel's life (invariant 6).
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
            if (!_uiSurfaces.Contains(QuickAccessSurface))
            {
                ClaimUiSurface(QuickAccessSurface);
            }
            _overlay.Activate();
            if (_touchSwipes is not null)
            {
                _touchSwipes.WatchTaps = true;
            }
            return;
        }

        var vm = new OverlayViewModel
        {
            ExplorerRunning = ExplorerControl.IsRunningInSession(),
            HomeAppAlive = _monitor?.IsAlive ?? false,
            HomeAppName = "Steam",
            GlyphStyle = _config.GlyphStyle,
            WarningText = _pendingWarning,
            ShowKeepAwake = _keepAwake is not null,
            ModeSwitchAvailable = !_previewOnly,
            KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off,
            KeepAwakeDownloadActive = _keepAwake?.DownloadHold ?? false,
        };
        ApplyCefVisibility(vm, _config);
        RefreshPowerTimeouts(vm);

        _overlayViewModel = vm;
        _overlay = new OverlayWindow(vm, UiScale());
        _overlay.AttachDeviceBridge(_device);
        _overlay.AttachPerformanceSource(_performance);
        _overlay.HomeAppRequested += () => { _suppressFocusRestore = true; CloseOverlay(); _modes.StartOrFocusSteam(); };
        _overlay.DesktopRequested += () =>
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
        _overlay.ExitBigPictureRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            _modes.ExitBigPicture();
        };
        _overlay.CloseLauncherRequested += () => { _modes.CloseSteam(); vm.HomeAppAlive = false; };
        _overlay.KeepAwakeToggleRequested += () =>
        {
            // The service's StateChanged callback (already subscribed) writes the
            // resulting state back into the view model; the indicator dot follows
            // on its own poll tick.
            _keepAwake?.CycleManualMode();
        };
        _overlay.PowerTimeoutCycleRequested += kind =>
        {
            // Registry-fast policy reads/writes — no off-thread hop needed. A failed
            // read leaves the row's badge at "—" rather than writing blind.
            var current = PowerTimeouts.Read(kind);
            if (current is not null)
            {
                PowerTimeouts.Write(kind, PowerTimeouts.NextPreset(current.Value));
            }
            RefreshPowerTimeouts(vm);
        };
        _overlay.TaskManagerRequested += () => { _suppressFocusRestore = true; CloseOverlay(); StartTaskManager(); };
        _overlay.SettingsRequested += () =>
        {
            _suppressFocusRestore = true;
            // Stop tap-outside watching immediately: the overlay lingers briefly on
            // its deferred close, and once Settings is up a tap on it must NOT read
            // as a tap outside the overlay and dismiss it — that dismissal refocuses
            // Steam and drops Settings behind Big Picture (device-reported).
            if (_touchSwipes is not null)
            {
                _touchSwipes.WatchTaps = false;
            }
            // Hand the lease to Settings instead of releasing it: the close below
            // keeps Steam's controller blocked continuously, so Settings inherits a
            // live lease with no release/re-inject churn.
            var settings = new SettingsWindow(gameModeSurface: true);
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
        // A modal system dialog (the custom launch action's file picker) is its own
        // window outside the bar's rectangle. Tap-outside dismissal is raw hit
        // testing, so every touch in that dialog would otherwise close the bar and
        // cancel the flow; the gamepad would likewise still be driving the bar
        // hidden behind it. Same suspension the Settings handoff performs, but
        // reversed when the dialog closes because the bar stays up underneath.
        _overlay.SystemDialogActive += active =>
        {
            if (active)
            {
                _dialogPriorWatchTaps = _touchSwipes?.WatchTaps ?? false;
                _dialogPriorNavigation = _navigation?.IsEnabled ?? false;
            }
            if (_touchSwipes is not null)
            {
                // Restore what was armed rather than assuming it: the bar is not
                // the only surface that owns tap watching.
                _touchSwipes.WatchTaps = !active && _dialogPriorWatchTaps && _overlay is not null;
            }
            if (_navigation is not null)
            {
                _navigation.IsEnabled = !active && _dialogPriorNavigation;
            }
        };
        // Dismiss never refocuses anything: Windows hands the foreground back to
        // the previous window on close. An explicit refocus-on-dismiss once yanked
        // Steam over an app the user had deliberately cycled to.
        _overlay.Dismissed += CloseOverlay;
        _overlay.Closed += (_, _) =>
        {
            ReleaseUiSurface(QuickAccessSurface);
            _closePending = false;
            _pendingClose = null;
            // Give Steam its pad back the moment the panel is gone — unless the
            // taskbar took over the surface and still needs the lease. A Settings
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
            else if (_taskbar is null)
            {
                ReleaseSteamInputLease();
            }
            var reopenForWarning = _reopenOverlayForWarning;
            _reopenOverlayForWarning = false;
            _navigation?.Dispose();
            _navigation = null;
            StopWakeLockRefresh();
            KeyboardService.Handler = null;
            _keyboardWindow?.Close();
            // Keep polling if the controller chord or the open taskbar still needs it.
            if (!(_config.GamepadChord.Enabled && _config.GamepadChord.Buttons != 0) && _taskbar is null)
            {
                _gamepad.Stop();
            }
            _overlay = null;
            _overlayViewModel = null;
            // Game mode only: call back the window that was focused before the
            // panel opened (exclusive-fullscreen games sit minimized by now).
            if (!_suppressFocusRestore && _restoreFocusTo != 0 && !ExplorerControl.IsRunningInSession())
            {
                Log.Info("Restoring previously focused window.");
                WindowFinder.BringToForeground(_restoreFocusTo);
            }
            _restoreFocusTo = 0;
            if (_touchSwipes is not null && _taskbar is null)
            {
                // TappedAt consumers are gone with the panel; stop the per-tap
                // dispatches until the next ShowOverlay.
                _touchSwipes.WatchTaps = false;
            }
            if (_taskbar is null)
            {
                ShowTouchEdges();
            }
            if (reopenForWarning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowOverlay);
            }
            else if (_taskbar is null)
            {
                // The shell goes invisible again — give the freed UI memory back
                // once the close (and any focus restore) has settled.
                _pendingTrim?.Dispose();
                _pendingTrim = RunOnUiThreadAfter(TimeSpan.FromSeconds(5),
                    () => MemoryTrim.TrimBestEffort("overlay closed"));
            }
        };

        _overlay.AttachFormatManager(FormatManager);
        _overlay.SubViewClosed += CloseKeyboardNow;

        var overlay = _overlay;
        _navigation = new GamepadNavigation(_uiInput, _overlay, OnOverlayBack,
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            preferredFocus: () => overlay.DefaultFocusTarget,
            tabPrevious: () => _overlay?.SelectPreviousTab(),
            tabNext: () => _overlay?.SelectNextTab(),
            onEdge: OnOverlayEdge);
        // The slim sidebar can't hold a keyboard; text entry pops the keyboard window
        // beside it. Registered while the overlay owns navigation.
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
            ReleaseUiSurface(QuickAccessSurface);
            throw;
        }
        RefreshWakeLockIndicator();
        StartWakeLockRefresh();
        if (_touchSwipes is not null)
        {
            _touchSwipes.WatchTaps = true;
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

    /// <summary>Tap-outside dismissal via the raw-input observer, for whichever
    /// surface is open. Deliberately NOT implemented as dismiss-on-deactivate: the
    /// window-switching actions hand the foreground to another window while the
    /// surface must stay open for further presses.</summary>
    private void OnTappedAt(int x, int y)
    {
        if (_overlay is not null)
        {
            if (!HitsWindow(_overlay, x, y)
                && (_keyboardWindow is null || !HitsWindow(_keyboardWindow, x, y)))
            {
                Log.Info("Touch outside quick access — dismissing.");
                CloseOverlay();
            }
            return;
        }
        // The radio panel sits ABOVE the bar, outside its rectangle. A tap in
        // it must not read as tap-outside; a tap anywhere else closes the
        // panel first and keeps the bar — one dismissal per tap, so a stray
        // touch can't tear down both surfaces at once.
        if (_radioPanel is not null)
        {
            if (!HitsWindow(_radioPanel, x, y))
            {
                Log.Info("Touch outside radio panel — dismissing.");
                CloseRadioPanel();
            }
            return;
        }
        if (_audioPanel is not null)
        {
            if (!HitsWindow(_audioPanel, x, y))
            {
                Log.Info("Touch outside audio panel — dismissing.");
                CloseAudioPanel();
            }
            return;
        }
        if (_ejectPanel is not null)
        {
            if (!HitsWindow(_ejectPanel, x, y))
            {
                Log.Info("Touch outside eject panel — dismissing.");
                CloseEjectPanel();
            }
            return;
        }
        if (_taskbar is not null && !HitsWindow(_taskbar, x, y))
        {
            Log.Info("Touch outside taskbar — dismissing.");
            CloseTaskbar();
        }
    }

    private static bool HitsWindow(Avalonia.Controls.Window window, int x, int y)
    {
        if (double.IsNaN(window.Width) || double.IsNaN(window.Height))
        {
            // Not measured yet — treat as hit so a tap can't dismiss a window
            // that is still coming up.
            return true;
        }
        // Window scaling, not the screens cache — the cache reports the
        // pre-game-mode factor after the runtime display-scale flip, which
        // inflates the hit box and swallows taps just outside the window.
        var scaling = window.DesktopScaling;
        var pos = window.Position;
        var w = (int)Math.Ceiling(window.Width * scaling);
        var h = (int)Math.Ceiling(window.Height * scaling);
        return x >= pos.X && x < pos.X + w && y >= pos.Y && y < pos.Y + h;
    }

    /// <summary>Window focused when the taskbar opened, and whether an action
    /// redirected focus (same discipline as the overlay's pair — invariant 6).</summary>
    private nint _taskbarRestoreFocusTo;
    private bool _taskbarSuppressFocusRestore;
    private bool _taskbarClosePending;
    private IDisposable? _pendingTaskbarClose;

    /// <summary>Shows and activates the game-mode taskbar (bottom-swipe surface):
    /// a thin centered strip of the switchable windows. Mutually exclusive with the
    /// quick-access overlay; whichever opens closes the other and inherits its
    /// focus-restore target.</summary>
    public void ShowTaskbar()
    {
        if (_disposed)
        {
            return;
        }
        OverlayShown?.Invoke();
        _pendingTrim?.Dispose();
        _pendingTrim = null;
        nint inheritedRestore = 0;
        if (_overlay is not null)
        {
            if (_keyboardWindow is not null)
            {
                _keyboardNavigation?.Dispose();
                _keyboardNavigation = null;
                _keyboardWindow.Close();
                _keyboardWindow = null;
                _overlay.KeyboardOwnsFocus = false;
            }
            inheritedRestore = _restoreFocusTo;
            _suppressFocusRestore = true;
            if (_navigation is not null)
            {
                _navigation.IsEnabled = false;
            }
            CloseOverlay();
        }
        AcquireSteamInputLease();
        HideTouchEdges();
        if (_taskbar is not null)
        {
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled =
                    _radioPanel is null && _audioPanel is null && _ejectPanel is null;
            }
            if (_taskbarClosePending)
            {
                // Re-summoned inside the deferred close — keep the window alive
                // (same race as the overlay's re-show).
                _pendingTaskbarClose?.Dispose();
                _pendingTaskbarClose = null;
                _taskbarClosePending = false;
                // The tile pick (or handover) that suppressed the restore was
                // abandoned with the close — the bar lives on and owes its opener a
                // focus restore again (invariant 6).
                _taskbarSuppressFocusRestore = false;
                Log.Info("Taskbar re-shown during deferred close — pending close cancelled.");
            }
            RefreshTaskbarEntries();
            if (!_uiSurfaces.Contains(TaskbarSurface))
            {
                ClaimUiSurface(TaskbarSurface);
            }
            _taskbar.Activate();
            if (_touchSwipes is not null)
            {
                _touchSwipes.WatchTaps = true;
            }
            return;
        }

        _taskbarRestoreFocusTo = inheritedRestore != 0 ? inheritedRestore : Interop.NativeMethods.GetForegroundWindow();
        _taskbarSuppressFocusRestore = false;

        // 48 px rasters downscale crisply into the 32-DIP tiles on high-DPI panels.
        _iconCache ??= new WindowIconCache(48);
        var vm = new TaskbarViewModel();
        _taskbarViewModel = vm;
        RefreshTaskbarEntries();
        Log.Info($"Taskbar shown ({vm.Entries.Count} windows).");

        OnTrayIconsChanged();
        // The bar's status cluster (clock/battery/Wi-Fi) lives only while the bar
        // is open; OnTaskbarClosed disposes it with the window.
        // Shares the session's audio manager when there is one, so the taskbar's audio tile and
        // Steam's audio namespace are the same state rather than two views that can disagree.
        _systemStatus = new SystemStatus(_sessionAudio, _sessionRadios);
        _systemStatus.Start();
        _taskbar = new TaskbarWindow(vm, _systemStatus, UiScale());
        // The home button rides the existing surface handover: ShowOverlay closes
        // the bar, inherits its restore target, and keeps the shared lease.
        _taskbar.HomeRequested += ShowOverlay;
        _taskbar.WindowPicked += PickTaskbarWindow;
        _taskbar.TrayIconActivated += OnTrayIconActivated;
        _taskbar.Dismissed += CloseTaskbar;
        _taskbar.RadioPanelRequested += ShowRadioPanel;
        _taskbar.AudioPanelRequested += ShowAudioPanel;
        _taskbar.EjectPanelRequested += ShowEjectPanel;
        _taskbar.Closed += (_, _) => OnTaskbarClosed();
        _taskbarNavigation = new GamepadNavigation(_uiInput, _taskbar, CloseTaskbar,
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            preferredFocus: () => _taskbar?.DefaultFocusTarget,
            secondary: focused => _taskbar?.RequestTrayContextMenu(focused));
        // The taskbar has no tab strip. Its navigation is paused whenever a
        // child panel covers it and during the 150 ms surface handover.
        _gamepad.Start();
        ClaimUiSurface(TaskbarSurface);
        try
        {
            _taskbar.Show();
            _taskbar.Activate();
        }
        catch
        {
            ReleaseUiSurface(TaskbarSurface);
            throw;
        }
        StartTaskbarRefresh();
        if (_touchSwipes is not null)
        {
            _touchSwipes.WatchTaps = true;
        }
    }

    /// <summary>Opens or closes the WSGM taskbar exactly once.</summary>
    public void ToggleTaskbar()
    {
        if (_taskbar is null)
        {
            ShowTaskbar();
        }
        else
        {
            CloseTaskbar();
        }
    }

    private RadioWindow? _radioPanel;
    private GamepadNavigation? _radioNavigation;
    private bool _radioClosePending;
    private IDisposable? _pendingRadioClose;

    private KeyboardWindow? _keyboardWindow;
    private GamepadNavigation? _keyboardNavigation;

    /// <summary>Opens the keyboard window beside the sidebar for one text field
    /// (<see cref="KeyboardService"/>). Gamepad focus moves to it; crossing back to the
    /// sidebar (D-pad right off its right edge) or accepting/cancelling returns focus.
    /// Runs on the UI thread. Returns true (the request is handled).</summary>
    private bool OpenKeyboard(string prompt, string initial, int maxLength, Action<string> onAccept)
    {
        _keyboardWindow?.Close();

        var overlay = _overlay;
        if (overlay is null)
        {
            return false;
        }
        var window = new KeyboardWindow(prompt, initial, maxLength, UiScale());
        _keyboardWindow = window;
        overlay.KeyboardOwnsFocus = true;
        window.Accepted += text => onAccept(text);
        // The window's own Opened handler (subscribed first) applies the UI-scale
        // LayoutTransform, which only changes Bounds on the NEXT layout pass — so the
        // positioning below must force that pass, and re-run when SizeToContent grows
        // the window afterwards, or the keyboard is placed for its unscaled size.
        window.Opened += (_, _) => PositionKeyboardBesideOverlay(window, overlay);
        window.SizeChanged += (_, _) => PositionKeyboardBesideOverlay(window, overlay);
        window.Show();

        _keyboardNavigation = new GamepadNavigation(_uiInput, window, () => window.Close(),
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            onEdge: OnKeyboardEdge);
        // Focus is in the keyboard now; the sidebar's nav stands down until we cross back.
        if (_navigation is not null)
        {
            _navigation.IsEnabled = false;
        }
        window.Closed += (_, _) =>
        {
            _keyboardNavigation?.Dispose();
            _keyboardNavigation = null;
            _keyboardWindow = null;
            if (_navigation is not null)
            {
                _navigation.IsEnabled = true;
            }
            overlay.Activate();
            overlay.DefaultFocusTarget.Focus(Avalonia.Input.NavigationMethod.Directional);
            // Keep the activation reset suppressed through the handoff itself.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => overlay.KeyboardOwnsFocus = false);
        };
        window.Activate();
        window.FocusDefault();
        return true;
    }

    private static void PositionKeyboardBesideOverlay(KeyboardWindow window, OverlayWindow overlay)
    {
        // Left of the sidebar (which is docked to the right edge), vertically centred.
        // Settle any pending layout first: the UI-scale LayoutTransform applied on open
        // invalidates measure, and Bounds only reflects it after a layout pass.
        window.UpdateLayout();
        var scaling = window.DesktopScaling;
        var widthPx = (int)Math.Ceiling(Math.Max(window.Bounds.Width, 300) * scaling);
        var heightPx = (int)Math.Ceiling(Math.Max(window.Bounds.Height, 200) * scaling);
        var overlayHeightPx = (int)Math.Ceiling(overlay.Bounds.Height * scaling);
        var y = overlay.Position.Y + Math.Max(0, (overlayHeightPx - heightPx) / 2);
        var x = overlay.Position.X - widthPx - 8;
        var screen = overlay.Screens.ScreenFromWindow(overlay);
        if (screen is not null)
        {
            // Right-align against the sidebar when clamping: if the keyboard is wider
            // than the free space, losing pixels on the LEFT keeps the edge the gamepad
            // crosses (keyboard right ↔ sidebar left) usable. Math.Clamp would throw
            // when the window exceeds the work area, so clamp by hand.
            var minX = screen.WorkingArea.X;
            var maxX = Math.Max(minX, overlay.Position.X - widthPx - 8);
            x = Math.Min(Math.Max(x, minX), maxX);
            var minY = screen.WorkingArea.Y;
            var maxY = Math.Max(minY, screen.WorkingArea.Bottom - heightPx);
            y = Math.Min(Math.Max(y, minY), maxY);
        }
        window.Position = new Avalonia.PixelPoint(x, y);
    }

    // The sidebar rows are a single column, so any Left press is at the left edge: if
    // the keyboard window is open beside it, hand focus over.
    private void OnOverlayEdge(Avalonia.Input.NavigationDirection direction)
    {
        if (direction == Avalonia.Input.NavigationDirection.Left && _keyboardWindow is not null)
        {
            CrossToKeyboard();
        }
    }

    // Crossing off the keyboard's right edge returns to the sidebar.
    private void OnKeyboardEdge(Avalonia.Input.NavigationDirection direction)
    {
        if (direction == Avalonia.Input.NavigationDirection.Right)
        {
            CrossToSidebar();
        }
    }

    private void CrossToKeyboard()
    {
        if (_keyboardWindow is null || _keyboardNavigation is null)
        {
            return;
        }
        if (_navigation is not null)
        {
            _navigation.IsEnabled = false;
        }
        var keyboard = _keyboardWindow;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_keyboardNavigation is not null && _keyboardWindow == keyboard)
            {
                _keyboardNavigation.IsEnabled = true;
                keyboard.Activate();
                keyboard.FocusDefault();
            }
        });
    }

    private void CrossToSidebar()
    {
        if (_overlay is null)
        {
            return;
        }
        if (_keyboardNavigation is not null)
        {
            _keyboardNavigation.IsEnabled = false;
        }
        if (_navigation is not null)
        {
            _navigation.IsEnabled = true;
        }
        _overlay.Activate();
        _overlay.DefaultFocusTarget.Focus(Avalonia.Input.NavigationMethod.Directional);
    }

    private void CloseKeyboardNow()
    {
        _keyboardNavigation?.Dispose();
        _keyboardNavigation = null;
        _keyboardWindow?.Close();
        _keyboardWindow = null;
        if (_overlay is not null)
        {
            _overlay.KeyboardOwnsFocus = false;
        }
    }

    /// <summary>Closes the radio panel through the same deferred path as the
    /// other two surfaces: the 150 ms grace lets the window's WndProc hook eat
    /// the touch-promotion ghost click (invariant 3). Closing immediately from
    /// the raw-touch callback destroys the window before the synthesized click
    /// arrives, and it then lands on whatever is underneath.</summary>
    private void CloseRadioPanel()
    {
        if (_radioPanel is null || _radioClosePending)
        {
            return;
        }
        _radioClosePending = true;
        _pendingRadioClose = RunOnUiThreadAfter(TimeSpan.FromMilliseconds(150), () =>
        {
            _radioClosePending = false;
            _pendingRadioClose = null;
            _radioPanel?.Close();
        });
    }

    /// <summary>Opens the Wi-Fi/Bluetooth panel above the taskbar.</summary>
    /// <param name="bluetooth">True to open on the Bluetooth tab.</param>
    private void ShowRadioPanel(bool bluetooth)
    {
        if (_audioPanel is not null)
        {
            _audioPanel.Close();
        }
        if (_ejectPanel is not null)
        {
            _ejectPanel.Close();
        }
        if (_radioPanel is not null)
        {
            if (_radioClosePending)
            {
                _pendingRadioClose?.Dispose();
                _pendingRadioClose = null;
                _radioClosePending = false;
            }
            // The tile carries which radio was tapped, so an open panel follows
            // it rather than leaving the user on the tab it opened with.
            _radioPanel.SelectTab(bluetooth);
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = false;
            }
            if (_radioNavigation is not null)
            {
                _radioNavigation.IsEnabled = true;
            }
            _radioPanel.Activate();
            return;
        }
        if (_systemStatus is null)
        {
            return;
        }
        Log.Info($"Radio panel opened ({(bluetooth ? "Bluetooth" : "Wi-Fi")}).");
        var panel = new RadioWindow(_systemStatus.Radios, bluetooth, UiScale());
        _radioPanel = panel;
        if (_taskbarNavigation is not null)
        {
            _taskbarNavigation.IsEnabled = false;
        }
        // Its own navigation instance: the panel holds focus while it is open,
        // and B must close the panel rather than the bar behind it.
        _radioNavigation = new GamepadNavigation(_uiInput, panel, () => panel.Close(),
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            tabPrevious: panel.SelectPreviousTab,
            tabNext: panel.SelectNextTab);
        panel.Closed += (_, _) =>
        {
            _radioNavigation?.Dispose();
            _radioNavigation = null;
            _radioPanel = null;
            _radioClosePending = false;
            _pendingRadioClose?.Dispose();
            _pendingRadioClose = null;
            Log.Info("Radio panel closed.");
            // Hand focus back to the bar, which is still open underneath.
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = _audioPanel is null && _ejectPanel is null;
            }
            _taskbar?.Activate();
        };
        panel.Show();
        // The bar's real top edge, not a height to subtract: it is a topmost
        // window rather than a registered appbar, so the screen's working area
        // does not account for it and computing the position from screen height
        // minus bar height left a visible gap.
        panel.DockAboveTaskbar(_taskbar?.Position.Y ?? 0);
        panel.Activate();
    }

    private AudioWindow? _audioPanel;
    private GamepadNavigation? _audioNavigation;
    private bool _audioClosePending;
    private IDisposable? _pendingAudioClose;

    /// <summary>Closes the audio panel after the touch-promotion grace window.</summary>
    private void CloseAudioPanel()
    {
        if (_audioPanel is null || _audioClosePending)
        {
            return;
        }
        _audioClosePending = true;
        _pendingAudioClose = RunOnUiThreadAfter(TimeSpan.FromMilliseconds(150), () =>
        {
            _audioClosePending = false;
            _pendingAudioClose = null;
            _audioPanel?.Close();
        });
    }

    /// <summary>Opens the master-volume and default-device panel above the taskbar.</summary>
    private void ShowAudioPanel()
    {
        if (_radioPanel is not null)
        {
            _radioPanel.Close();
        }
        if (_ejectPanel is not null)
        {
            _ejectPanel.Close();
        }
        if (_audioPanel is not null)
        {
            if (_audioClosePending)
            {
                _pendingAudioClose?.Dispose();
                _pendingAudioClose = null;
                _audioClosePending = false;
            }
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = false;
            }
            if (_audioNavigation is not null)
            {
                _audioNavigation.IsEnabled = true;
            }
            _audioPanel.Activate();
            return;
        }
        if (_systemStatus is null)
        {
            return;
        }

        Log.Info("Audio panel opened.");
        var panel = new AudioWindow(_systemStatus.Audio, UiScale());
        _audioPanel = panel;
        if (_taskbarNavigation is not null)
        {
            _taskbarNavigation.IsEnabled = false;
        }
        _audioNavigation = new GamepadNavigation(_uiInput, panel, () => panel.Close(),
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            preferredFocus: () => panel.DefaultFocusTarget);
        panel.Closed += (_, _) =>
        {
            _audioNavigation?.Dispose();
            _audioNavigation = null;
            _audioPanel = null;
            _audioClosePending = false;
            _pendingAudioClose?.Dispose();
            _pendingAudioClose = null;
            Log.Info("Audio panel closed.");
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = _radioPanel is null && _ejectPanel is null;
            }
            _taskbar?.Activate();
        };
        panel.Show();
        panel.DockAboveTaskbar(_taskbar?.Position.Y ?? 0);
        panel.Activate();
    }

    private EjectWindow? _ejectPanel;
    private GamepadNavigation? _ejectNavigation;
    private bool _ejectClosePending;
    private IDisposable? _pendingEjectClose;

    /// <summary>Closes the Safe Eject panel after the touch-promotion grace
    /// window (invariant 3).</summary>
    private void CloseEjectPanel()
    {
        if (_ejectPanel is null || _ejectClosePending)
        {
            return;
        }
        _ejectClosePending = true;
        _pendingEjectClose = RunOnUiThreadAfter(TimeSpan.FromMilliseconds(150), () =>
        {
            _ejectClosePending = false;
            _pendingEjectClose = null;
            _ejectPanel?.Close();
        });
    }

    /// <summary>Opens the Safe Eject panel above the taskbar.</summary>
    private void ShowEjectPanel()
    {
        if (_radioPanel is not null)
        {
            _radioPanel.Close();
        }
        if (_audioPanel is not null)
        {
            _audioPanel.Close();
        }
        if (_ejectPanel is not null)
        {
            if (_ejectClosePending)
            {
                _pendingEjectClose?.Dispose();
                _pendingEjectClose = null;
                _ejectClosePending = false;
            }
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = false;
            }
            if (_ejectNavigation is not null)
            {
                _ejectNavigation.IsEnabled = true;
            }
            _ejectPanel.Activate();
            return;
        }
        if (_systemStatus is null)
        {
            return;
        }

        Log.Info("Eject panel opened.");
        var panel = new EjectWindow(_systemStatus.Drives, UiScale());
        _ejectPanel = panel;
        if (_taskbarNavigation is not null)
        {
            _taskbarNavigation.IsEnabled = false;
        }
        _ejectNavigation = new GamepadNavigation(_uiInput, panel, () => panel.Close(),
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo);
        panel.Closed += (_, _) =>
        {
            _ejectNavigation?.Dispose();
            _ejectNavigation = null;
            _ejectPanel = null;
            _ejectClosePending = false;
            _pendingEjectClose?.Dispose();
            _pendingEjectClose = null;
            Log.Info("Eject panel closed.");
            if (_taskbarNavigation is not null)
            {
                _taskbarNavigation.IsEnabled = _radioPanel is null && _audioPanel is null;
            }
            _taskbar?.Activate();
        };
        panel.Show();
        panel.DockAboveTaskbar(_taskbar?.Position.Y ?? 0);
        panel.Activate();
    }

    /// <summary>The desktop-DPI factor for WSGM surfaces. The boost exists ONLY
    /// to compensate game mode's forced 100% display scaling — in desktop mode
    /// the display already runs at the user's real scaling and Avalonia applies
    /// it, so boosting again would double up (device-reported: surfaces rendered
    /// huge on a 100% desktop when the recommended-scale fallback fired there).</summary>
    private double UiScale()
        => ExplorerControl.IsRunningInSession()
            ? 1.0
            : DisplayScale.GetUiScalePercent(_config) / 100.0;

    /// <summary>Attaches (or detaches, with null) the game-mode tray host whose
    /// icons render in the bar's tray area. ShellSession owns the host's
    /// lifecycle — created per game-mode span, destroyed before explorer starts.</summary>
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
        => _taskbarViewModel?.ReconcileTray(_trayHost?.Table.Icons ?? []);

    private System.Collections.Generic.HashSet<uint> _steamPids = [];
    private DateTime _steamPidsAtUtc;

    /// <summary>Rebuilds/updates the tile collection in place. While the bar is
    /// open the foreground window is the bar itself, so the highlight uses the
    /// captured pre-open foreground instead.</summary>
    private void RefreshTaskbarEntries()
    {
        if (_taskbarViewModel is null)
        {
            return;
        }
        // Steam's pid set barely moves within a bar session, but resolving it
        // snapshots the whole process table — on the UI thread that also drives the
        // 16 ms gamepad poll and the tile focus. Re-read at the SteamMonitor's own
        // 5 s cadence instead of on every 1 s tile refresh.
        var now = DateTime.UtcNow;
        if (_steamPidsAtUtc == default || now - _steamPidsAtUtc >= TimeSpan.FromSeconds(5))
        {
            _steamPids = WindowFinder.FindProcessIds(Steam.ProcessNames);
            _steamPidsAtUtc = now;
        }
        var steamPids = _steamPids;
        var active = _taskbar is { IsVisible: true }
            ? _taskbarRestoreFocusTo
            : Interop.NativeMethods.GetForegroundWindow();
        _taskbarViewModel.Reconcile(
            WindowFinder.ListSwitchableWindows(),
            active,
            window =>
            {
                // Cached icons are handed over synchronously; a miss resolves off the
                // UI thread (cross-process WM_GETICON probes plus a possible exe read)
                // and lands on the tile in place when it arrives.
                Avalonia.Media.Imaging.Bitmap? icon = null;
                if (_iconCache is not null && !_iconCache.TryGetCached(window.Hwnd, out icon))
                {
                    _iconCache.ResolveInBackground(window.Hwnd, window.ProcessId, ApplyResolvedIcon);
                }
                return new TaskbarEntry(
                    window.Hwnd,
                    window.Title,
                    steamPids.Contains(window.ProcessId),
                    icon);
            });
    }

    /// <summary>Places a background-resolved icon on its tile, if that tile is still on
    /// the open bar. Runs off the UI thread, so it marshals before touching view state.</summary>
    private void ApplyResolvedIcon(nint hwnd, Avalonia.Media.Imaging.Bitmap? icon)
    {
        if (icon is null)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // The window may have closed, or the bar may have been dismissed and its
            // cache cleared, between the resolve starting and finishing.
            if (_taskbarViewModel is null || _taskbar is not { IsVisible: true })
            {
                return;
            }
            foreach (var entry in _taskbarViewModel.Entries)
            {
                if (entry.Hwnd == hwnd)
                {
                    entry.Icon = icon;
                    return;
                }
            }
        });
    }

    /// <summary>Keeps the open bar current (new/closed windows, titles, minimize
    /// state) without disturbing the focused tile — Reconcile updates in place.</summary>
    private void StartTaskbarRefresh()
    {
        StopTaskbarRefresh();
        // Parameterless ctor + explicit Start (invariant 4).
        _taskbarRefresh = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _taskbarRefresh.Tick += (_, _) => RefreshTaskbarEntries();
        _taskbarRefresh.Start();
    }

    private void StopTaskbarRefresh()
    {
        _taskbarRefresh?.Stop();
        _taskbarRefresh = null;
    }

    private IDisposable? _pendingTopmostRestore;

    /// <summary>Forwards a tray-icon activation to its owner. For context menus
    /// the bar additionally drops Topmost for a while: WinForms tray menus shown
    /// via plain Show() (Handheld Companion since its commit c86932bc) are
    /// NON-topmost and never activated — over a topmost bar they open BEHIND it,
    /// which reads as "the menu doesn't appear" (device-reported).</summary>
    private void OnTrayIconActivated(TrayIconEntry entry, bool contextMenu, Avalonia.PixelPoint anchor)
    {
        if (contextMenu)
        {
            if (_taskbar is not null)
            {
                _taskbar.Topmost = false;
                _pendingTopmostRestore?.Dispose();
                _pendingTopmostRestore = RunOnUiThreadAfter(TimeSpan.FromSeconds(10), () =>
                {
                    _pendingTopmostRestore = null;
                    if (_taskbar is not null)
                    {
                        _taskbar.Topmost = true;
                    }
                });
            }
        }
        else
        {
            // A plain activation opens/shows the owning app — dismiss the bar so it
            // comes forward (same rule as picking a window tile). A context-menu
            // request keeps the bar: the menu pops over it.
            _taskbarSuppressFocusRestore = true;
            CloseTaskbar();
        }
        _trayHost?.SendClick(entry.Icon, contextMenu, anchor.X, anchor.Y);
    }

    private void OnTaskbarClosed()
    {
        ReleaseUiSurface(TaskbarSurface);
        // The panel is a child of the bar in everything but parenthood: it
        // binds the SystemStatus disposed below and runs its own gamepad
        // navigation. Leaving it alive past its bar (quick access opened by
        // hotkey or edge swipe takes this path) left a topmost window bound to
        // a disposed manager, competing for controller input with the overlay.
        // Closed directly, not deferred: the bar is already going.
        if (_radioPanel is not null)
        {
            Log.Info("Taskbar closed with the radio panel open — closing the panel.");
            _radioPanel.Close();
        }
        if (_audioPanel is not null)
        {
            Log.Info("Taskbar closed with the audio panel open — closing the panel.");
            _audioPanel.Close();
        }
        if (_ejectPanel is not null)
        {
            Log.Info("Taskbar closed with the eject panel open — closing the panel.");
            _ejectPanel.Close();
        }
        _taskbarClosePending = false;
        _pendingTaskbarClose = null;
        _pendingTopmostRestore?.Dispose();
        _pendingTopmostRestore = null;
        StopTaskbarRefresh();
        _taskbarNavigation?.Dispose();
        _taskbarNavigation = null;
        if (_overlay is null)
        {
            ReleaseSteamInputLease();
        }
        if (!(_config.GamepadChord.Enabled && _config.GamepadChord.Buttons != 0) && _overlay is null)
        {
            _gamepad.Stop();
        }
        _taskbar = null;
        _taskbarViewModel = null;
        _systemStatus?.Dispose();
        _systemStatus = null;
        // Free the rasterized icons with the bar; the next open re-resolves.
        _iconCache?.Clear();
        // Same for the cached Steam pid set: the next bar session starts fresh.
        _steamPidsAtUtc = default;
        // Game mode only, and only when no tile pick redirected focus (invariant 6).
        if (!_taskbarSuppressFocusRestore && _taskbarRestoreFocusTo != 0 && !ExplorerControl.IsRunningInSession())
        {
            Log.Info("Restoring previously focused window (taskbar).");
            WindowFinder.BringToForeground(_taskbarRestoreFocusTo);
        }
        _taskbarRestoreFocusTo = 0;
        if (_touchSwipes is not null && _overlay is null)
        {
            _touchSwipes.WatchTaps = false;
        }
        if (_overlay is null)
        {
            ShowTouchEdges();
            _pendingTrim?.Dispose();
            _pendingTrim = RunOnUiThreadAfter(TimeSpan.FromSeconds(5),
                () => MemoryTrim.TrimBestEffort("taskbar closed"));
        }
    }

    /// <summary>Closes the taskbar through the same deferred path as the overlay
    /// (the 150 ms grace lets the window's hook eat the touch-promotion ghost
    /// click — invariant 3).</summary>
    private void CloseTaskbar()
    {
        if (_taskbar is null || _taskbarClosePending)
        {
            return;
        }
        _taskbarClosePending = true;
        _pendingTaskbarClose = RunOnUiThreadAfter(TimeSpan.FromMilliseconds(150), () =>
        {
            _taskbarClosePending = false;
            _pendingTaskbarClose = null;
            _taskbar?.Close();
        });
    }

    private bool _closePending;
    private IDisposable? _pendingClose;
    private IDisposable? _pendingTrim;

    /// <summary>The single idiom for delayed UI-thread work in this controller
    /// (deferred close, auto-relaunch, Task Manager focus polling). Runs the action
    /// on the UI thread after the delay; dispose the returned handle to cancel.
    /// UI-thread callers only — overlay events and SteamMonitor's tick already are.</summary>
    private static IDisposable RunOnUiThreadAfter(TimeSpan delay, Action action)
        => Avalonia.Threading.DispatcherTimer.RunOnce(action, delay);

    private void CloseOverlay()
    {
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
        _pendingClose = RunOnUiThreadAfter(TimeSpan.FromMilliseconds(150), () =>
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

    /// <summary>Releases overlay windows, input activation, and lifecycle subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CloseKeyboardNow();
        // Deliberately NOT retracting the injected Steam UI (tabs, badge, Wi-Fi AP)
        // here: the only caller of this Dispose is the Settings preview controller,
        // and retracting would tear the LIVE session's tabs out of Big Picture.
        // ShellSession owns that teardown and awaits it in ApplyCefMasterSwitch.
        AttachTrayHost(null);
        _modes.SteamStartFailed -= WarnOrReopen;
        SteamInputBlocker.RecoveryWarningRaised -= OnSteamInputRecoveryWarning;
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
        StopTaskbarRefresh();
        if (_overlay is not null || _taskbar is not null)
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
        CloseOverlay();
        // The bar's deferred Closed handler clears the icon cache; disposing it
        // here would leave the still-open window rendering disposed bitmaps for
        // the 150 ms grace.
        CloseTaskbar();
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
        if (_touchSwipes is not null)
        {
            _touchSwipes.Triggered -= OnSwipeTriggered;
            _touchSwipes.TappedAt -= OnTappedAt;
            _touchSwipes.Dispose();
            _touchSwipes = null;
        }
    }
}
