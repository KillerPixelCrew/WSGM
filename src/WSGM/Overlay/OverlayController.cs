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
    private readonly object _homeLaunchGate = new();
    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);

    public OverlayController(AppConfig config, SteamMonitor? monitor)
    {
        _config = config;
        _monitor = monitor;

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
            System.Threading.Tasks.Task.Delay(10_000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
                    StartOrFocusSteam();
                }));
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

    /// <summary>Asks Steam to leave Big Picture (Steam keeps running). No-op if
    /// Steam isn't running.</summary>
    private void ExitBigPicture()
    {
        // Live check, not the up-to-5 s-stale monitor poll: entering desktop mode
        // right after Steam started must still send the close URL.
        if (!Steam.IsRunning)
        {
            return;
        }
        Log.Info("Exiting Steam Big Picture.");
        AppLauncher.StartProtocol(Steam.CloseBigPictureUrl);
    }

    /// <summary>Desktop mode: stop reacting to Steam (no auto-relaunch, no overlay
    /// pop), drop Steam out of Big Picture, bring the desktop up.</summary>
    private void EnterDesktopMode()
    {
        Log.Info("Entering desktop mode.");
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        ExitBigPicture();
        SlateMode.ApplyDesktopMode(_config);
        DisplayScale.RestoreSaved(_config);
        ExplorerControl.StartExplorer();
    }

    /// <summary>Game mode: desktop goes away, monitoring resumes, Big Picture comes
    /// back (the protocol also boots Steam if it exited while on the desktop).</summary>
    private void EnterGameMode()
    {
        Log.Info("Entering game mode.");
        ExplorerControl.KillExplorer();
        SlateMode.ApplyGameMode(_config);
        DisplayScale.ApplyGameMode(_config);
        if (_monitor is not null)
        {
            _monitor.Paused = false;
        }
        StartOrFocusSteam();
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
            FocusSteam();
        }
        else
        {
            WindowFinder.BringToForeground(entry.Hwnd);
        }
    }

    private static void StartTaskManager()
    {
        try
        {
            var taskmgr = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(taskmgr) { UseShellExecute = true });
            Log.Info("Started Task Manager.");

            // It opens while our focused panel is closing, so the game underneath
            // reclaims the foreground and Task Manager lands behind it. Wait for
            // its window and promote it.
            System.Threading.Tasks.Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    await System.Threading.Tasks.Task.Delay(300);
                    var hwnd = WindowFinder.FindWindow("Taskmgr", windowClass: null);
                    if (hwnd != 0)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => WindowFinder.BringToForeground(hwnd));
                        return;
                    }
                }
                Log.Warn("Task Manager window not found to focus.");
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start Task Manager", ex);
        }
    }

    /// <summary>Deliberately stops Steam (graceful steam://exit). Pauses the monitor
    /// first so neither auto-relaunch nor the exit-overlay reaction fires.</summary>
    private void CloseSteam(OverlayViewModel vm)
    {
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        Log.Info("Closing Steam (steam://exit).");
        AppLauncher.StartProtocol(Steam.ExitUrl);
        vm.HomeAppAlive = false;
    }

    /// <summary>Window focused when the overlay opened. Exclusive-fullscreen games
    /// minimize the moment our panel takes focus — closing the panel calls them
    /// back (restore + foreground), unless an overlay action redirected focus.</summary>
    private nint _restoreFocusTo;
    private bool _suppressFocusRestore;

    public void ShowOverlay()
    {
        if (_disposed)
        {
            return;
        }
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
        _overlay.HomeAppRequested += () => { _suppressFocusRestore = true; CloseOverlay(); StartOrFocusSteam(); };
        _overlay.DesktopRequested += () =>
        {
            var explorerRunning = ExplorerControl.IsRunningInSession();
            _suppressFocusRestore = true;
            CloseOverlay();
            if (explorerRunning)
            {
                EnterGameMode();
            }
            else
            {
                EnterDesktopMode();
            }
        };
        _overlay.ExitBigPictureRequested += () =>
        {
            _suppressFocusRestore = true;
            CloseOverlay();
            ExitBigPicture();
        };
        _overlay.CloseLauncherRequested += () => CloseSteam(vm);
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
        _pendingClose = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _closePending = false;
            _pendingClose = null;
            _overlay?.Close();
        }, TimeSpan.FromMilliseconds(150));
    }

    /// <summary>Start and focus are the same operation: steam://open/bigpicture
    /// re-activates a running Big Picture (UIPI-proof) and boots Steam when it
    /// isn't running. Re-arms the monitor (desktop mode and close-Steam pause it).</summary>
    private void StartOrFocusSteam()
    {
        if (_monitor is not null)
        {
            _monitor.Paused = false;
        }
        if (_monitor?.IsAlive == true)
        {
            FocusSteam();
            return;
        }

        if (!TryBeginHomeLaunch())
        {
            return;
        }

        try
        {
            if (!Steam.IsInstalled)
            {
                WarnOrReopen("Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.");
                return;
            }
            var result = Steam.LaunchBigPicture();
            if (!result.Started)
            {
                WarnOrReopen("Couldn't start Steam Big Picture.");
            }
        }
        finally
        {
            EndHomeLaunch();
        }
    }

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

    private bool TryBeginHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            if (_homeLaunchInProgress || DateTime.UtcNow - _lastHomeLaunchUtc < HomeLaunchCooldown)
            {
                Log.Warn("Skipping duplicate home-app start request.");
                return false;
            }
            _homeLaunchInProgress = true;
            return true;
        }
    }

    private void EndHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            _homeLaunchInProgress = false;
            _lastHomeLaunchUtc = DateTime.UtcNow;
        }
    }

    private void FocusSteam()
    {
        if (_monitor?.IsAlive == true)
        {
            // Protocol re-activation self-focuses even against an elevated target.
            AppLauncher.StartProtocol(Steam.OpenBigPictureUrl);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
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
        // deferral exists for.
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
