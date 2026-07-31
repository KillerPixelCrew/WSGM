using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>The pin is scoped to the overlay's lifetime. While Steam's own
    /// window is foreground with a forced appid, Steam treats the pad as in-game
    /// input and Big Picture stops responding (user-reported) — so the pin exists
    /// only while OUR focused panel needs the pad, and is released on close.</summary>
    private void ApplySteamInputPin()
        => SteamInputPin.Apply(Math.Max(_config.SteamForceInputAppId, 0));

    private void ReleaseSteamInputPin() => SteamInputPin.Apply(0);

    /// <summary>Asks Steam to leave Big Picture (Steam keeps running). No-op if
    /// Steam isn't running.</summary>
    private void ExitBigPicture()
    {
        if (_monitor?.IsAlive != true)
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
        ExplorerControl.StartExplorer();
    }

    /// <summary>Game mode: desktop goes away, monitoring resumes, Big Picture comes
    /// back (the protocol also boots Steam if it exited while on the desktop).</summary>
    private void EnterGameMode()
    {
        Log.Info("Entering game mode.");
        ExplorerControl.KillExplorer();
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

    public void ShowOverlay()
    {
        ApplySteamInputPin();
        HideTouchEdges();
        if (_overlay is not null)
        {
            _overlayViewModel?.WarningText = _pendingWarning;
            _overlay.Activate();
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
        _overlay.HomeAppRequested += () => { CloseOverlay(); StartOrFocusSteam(); };
        _overlay.DesktopRequested += () =>
        {
            var explorerRunning = ExplorerControl.IsRunningInSession();
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
            CloseOverlay();
            ExitBigPicture();
        };
        _overlay.CloseLauncherRequested += () => CloseSteam(vm);
        _overlay.SwitchAppsRequested += () => ToggleWindowList(vm);
        _overlay.WindowPicked += PickWindow;
        _overlay.TaskManagerRequested += () => { CloseOverlay(); StartTaskManager(); };
        _overlay.SettingsRequested += () =>
        {
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
        _closePending = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _closePending = false;
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
        // Pin release happens in Program's exit/recovery paths, not here: a second
        // controller instance (Settings' overlay test) disposing must not unpin
        // the shell's live session.
        if (_monitor is not null)
        {
            _monitor.SteamExited -= OnSteamExited;
        }
        _hotkey.Dispose();
        _chordWatcher.Dispose();
        _gamepad.Dispose();
        DisposeTouchEdges();
        _overlay?.Close();
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
            _touchSwipes.Dispose();
            _touchSwipes = null;
        }
    }
}
