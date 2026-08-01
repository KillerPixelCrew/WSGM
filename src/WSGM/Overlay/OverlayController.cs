using System;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Owns the overlay activation surfaces (hotkey, raw-input touch swipes) and the
/// overlay window itself. Single entry point: ShowOverlay().</summary>
public sealed class OverlayController : IDisposable
{
    private AppConfig _config;
    private readonly SteamMonitor? _monitor;
    private readonly SessionModes _modes;
    private readonly HotkeyService _hotkey;
    private readonly GamepadService _gamepad = new();
    private readonly GamepadChordWatcher _chordWatcher;
    private TouchSwipeMonitor? _touchSwipes;
    private OverlayWindow? _overlay;
    private OverlayViewModel? _overlayViewModel;
    private GamepadNavigation? _navigation;
    private string _pendingWarning = "";
    private bool _reopenOverlayForWarning;
    private bool _disposed;

    /// <summary>Creates the overlay controller and its input activation surfaces.</summary>
    /// <param name="config">The initial shell configuration.</param>
    /// <param name="monitor">The optional Steam lifecycle monitor shared by the shell.</param>
    /// <param name="modes">The session-mode coordinator that performs requested transitions.</param>
    public OverlayController(AppConfig config, SteamMonitor? monitor, SessionModes modes)
    {
        _config = config;
        _monitor = monitor;
        _modes = modes;
        _modes.SteamStartFailed += WarnOrReopen;

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

        if (_overlay is not null)
        {
            HideTouchEdges();
        }
        else
        {
            ShowTouchEdges();
        }
    }

    private void OnSwipeTriggered(ScreenEdge edge) => ShowOverlay();

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

    /// <summary>Applies a freshly loaded config (settings saved in another process).</summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
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
        }
        if (_overlay is not null)
        {
            ApplySteamInputPin();
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

    private bool _pinReleased;

    /// <summary>The pin is scoped to the overlay's lifetime. While Steam's own
    /// window is foreground with a forced appid, Steam treats the pad as in-game
    /// input and Big Picture stops responding (user-reported) — so the pin exists
    /// only while OUR focused panel needs the pad, and is released on close.</summary>
    private void ApplySteamInputPin()
    {
        _pinReleased = false;
        SteamInputPin.Apply(Math.Max(_config.SteamForceInputAppId, 0));
    }

    /// <summary>At most one release per pin apply from THIS controller: Dispose
    /// releases early (see there), and the overlay's deferred Closed handler must
    /// then not fire a second /0 — a replacement controller may already have
    /// re-applied the pin by the time the 150 ms close lands.</summary>
    private void ReleaseSteamInputPin()
    {
        if (_pinReleased)
        {
            return;
        }
        _pinReleased = true;
        SteamInputPin.Apply(0);
    }

    /// <summary>Populates/toggles the Switch-app picker: alt-tab-style list of the
    /// actual switchable windows. There is no taskbar in shell mode, so this is
    /// the only way to move between running programs.</summary>
    private void ToggleWindowList(OverlayViewModel vm)
    {
        if (vm.ShowWindowList)
        {
            vm.ShowWindowList = false;
            return;
        }
        var steamPids = WindowFinder.FindProcessIds(Steam.ProcessNames);
        vm.SwitchableWindows.Clear();
        foreach (var window in WindowFinder.ListSwitchableWindows())
        {
            vm.SwitchableWindows.Add(new AppWindowEntry(window.Hwnd, window.Title, steamPids.Contains(window.ProcessId)));
        }
        if (vm.SwitchableWindows.Count == 0)
        {
            Log.Info("Switch app: no windows to show.");
            return;
        }
        vm.ShowWindowList = true;
    }

    /// <summary>Picking a window dismisses the panel and brings the app forward
    /// (Steam via the UIPI-proof protocol).</summary>
    private void PickWindow(AppWindowEntry entry)
    {
        Log.Info($"Switch app: focusing '{entry.Title}'.");
        _suppressFocusRestore = true;
        CloseOverlay();
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
            var hwnd = WindowFinder.FindWindow("Taskmgr", windowClass: null);
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

    /// <summary>Raised whenever quick access comes up (hotkey, swipe, chord,
    /// Steam-exit pop, warning reopen). The boot splash dismisses on it — the
    /// panel always outranks the splash.</summary>
    public event Action? OverlayShown;

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
        if (_overlay is null)
        {
            _restoreFocusTo = Interop.NativeMethods.GetForegroundWindow();
            _suppressFocusRestore = false;
        }
        ApplySteamInputPin();
        HideTouchEdges();
        if (_overlay is not null)
        {
            if (_closePending)
            {
                // Re-summoned inside the 150 ms deferred close: cancel the pending
                // Close() and keep the window — otherwise the timer would destroy
                // the just-reactivated panel and release the pin under it.
                _pendingClose?.Dispose();
                _pendingClose = null;
                _closePending = false;
                Log.Info("Overlay re-shown during deferred close — pending close cancelled.");
            }
            if (_overlayViewModel is not null)
            {
                _overlayViewModel.WarningText = _pendingWarning;
                // Recompute what the fresh-open path computes — Steam may have died
                // or the desktop may have changed while the panel stayed open.
                _overlayViewModel.ExplorerRunning = ExplorerControl.IsRunningInSession();
                _overlayViewModel.HomeAppAlive = _monitor?.IsAlive ?? false;
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
        };

        _overlayViewModel = vm;
        _overlay = new OverlayWindow(vm);
        _overlay.HomeAppRequested += () => { _suppressFocusRestore = true; CloseOverlay(); _modes.StartOrFocusSteam(); };
        _overlay.DesktopRequested += () =>
        {
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
        _overlay.SwitchAppsRequested += () => ToggleWindowList(vm);
        _overlay.WindowPicked += PickWindow;
        _overlay.TaskManagerRequested += () => { _suppressFocusRestore = true; CloseOverlay(); StartTaskManager(); };
        _overlay.SettingsRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            // A shell session normally has no main window. Opening settings in this
            // process keeps quick access responsive and avoids starting a second shell.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => new SettingsWindow().Show());
        };
        // Dismiss never refocuses anything: Windows hands the foreground back to
        // the previous window on close. An explicit refocus-on-dismiss once yanked
        // Steam over an app the user had deliberately cycled to.
        _overlay.Dismissed += CloseOverlay;
        _overlay.Closed += (_, _) =>
        {
            _closePending = false;
            _pendingClose = null;
            // Give Steam its pad back the moment the panel is gone.
            ReleaseSteamInputPin();
            var reopenForWarning = _reopenOverlayForWarning;
            _reopenOverlayForWarning = false;
            _navigation?.Dispose();
            _navigation = null;
            // Keep polling if the controller chord still needs to be watched.
            if (!(_config.GamepadChord.Enabled && _config.GamepadChord.Buttons != 0))
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
            if (_touchSwipes is not null)
            {
                // TappedAt consumers are gone with the panel; stop the per-tap
                // dispatches until the next ShowOverlay.
                _touchSwipes.WatchTaps = false;
            }
            ShowTouchEdges();
            if (reopenForWarning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowOverlay);
            }
            else
            {
                // The shell goes invisible again — give the freed UI memory back
                // once the close (and any focus restore) has settled.
                _pendingTrim?.Dispose();
                _pendingTrim = RunOnUiThreadAfter(TimeSpan.FromSeconds(5),
                    () => MemoryTrim.TrimBestEffort("overlay closed"));
            }
        };

        var overlay = _overlay;
        _navigation = new GamepadNavigation(_gamepad, _overlay, CloseOverlay,
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo,
            preferredFocus: () => overlay.DefaultFocusTarget);
        _gamepad.Start();
        _overlay.Show();
        // Game-Bar-style: the game stops receiving input while the panel is up.
        // Safe because SteamInputPin keeps the pad readable despite focus.
        _overlay.Activate();
        if (_touchSwipes is not null)
        {
            _touchSwipes.WatchTaps = true;
        }
    }

    /// <summary>Tap-outside dismissal via the raw-input observer. Deliberately NOT
    /// implemented as dismiss-on-deactivate: the Next-app button hands the
    /// foreground to another window while the panel must stay open for further
    /// presses.</summary>
    private void OnTappedAt(int x, int y)
    {
        var overlay = _overlay;
        if (overlay is null || double.IsNaN(overlay.Width) || double.IsNaN(overlay.Height))
        {
            return;
        }
        var scaling = overlay.Screens?.Primary?.Scaling ?? 1.0;
        var pos = overlay.Position;
        var w = (int)Math.Ceiling(overlay.Width * scaling);
        var h = (int)Math.Ceiling(overlay.Height * scaling);
        if (x >= pos.X && x < pos.X + w && y >= pos.Y && y < pos.Y + h)
        {
            return;
        }
        Log.Info("Touch outside quick access — dismissing.");
        CloseOverlay();
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
        _modes.SteamStartFailed -= WarnOrReopen;
        if (_monitor is not null)
        {
            _monitor.SteamExited -= OnSteamExited;
        }
        _hotkey.Dispose();
        _chordWatcher.Dispose();
        _gamepad.Dispose();
        DisposeTouchEdges();
        if (_overlay is not null)
        {
            // This controller owes a pin release (its overlay is open / pending
            // close). Fire it NOW, not in the deferred Closed handler 150 ms from
            // here: a replacement controller (Test panel pressed again) may apply
            // the pin in between, and a late /0 would unpin its live overlay.
            // ReleaseSteamInputPin's guard makes the Closed handler's release a
            // no-op afterwards, so the release fires exactly once. With no overlay
            // open there is nothing to release — a stray /0 here would unpin the
            // shell's live session (Settings' test controller disposes on window
            // close). Program's exit/recovery paths still release unconditionally.
            ReleaseSteamInputPin();
        }
        // Close through the same deferred path as every dismissal: an immediate
        // Close() would skip the 150 ms grace and bring back the ghost clicks the
        // deferral exists for. When Dispose runs during process exit the
        // dispatcher may stop pumping before the 150 ms lands and the Close()
        // never runs — deliberately fine: the pin was already released
        // synchronously above, and process exit destroys the window anyway.
        CloseOverlay();
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
