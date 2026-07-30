using System;
using OpenFSE.Core;
using OpenFSE.Input;
using OpenFSE.Interop;
using OpenFSE.Shell;

namespace OpenFSE.Overlay;

/// <summary>Owns the overlay activation surfaces (hotkey, edge strips) and the overlay
/// window itself. Single entry point: ShowOverlay().</summary>
public sealed class OverlayController : IDisposable
{
    private AppConfig _config;
    private readonly HomeAppMonitor? _monitor;
    private readonly HotkeyService _hotkey;
    private readonly GamepadService _gamepad = new();
    private readonly GamepadChordWatcher _chordWatcher;
    private EdgeSwipeWindow? _bottomStrip;
    private EdgeSwipeWindow? _rightStrip;
    private OverlayWindow? _overlay;
    private OverlayViewModel? _overlayViewModel;
    private GamepadNavigation? _navigation;
    private string _pendingWarning = "";
    private bool _reopenOverlayForWarning;
    private readonly object _homeLaunchGate = new();
    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);

    public OverlayController(AppConfig config, HomeAppMonitor? monitor)
    {
        _config = config;
        _monitor = monitor;

        _hotkey = new HotkeyService(MessageWindow.Create());
        _hotkey.Pressed += ShowOverlay;
        _hotkey.Apply(config.Hotkey);

        // Controller chord: needs polling even with no OpenFSE window on screen.
        _chordWatcher = new GamepadChordWatcher(_gamepad, config.GamepadChord);
        _chordWatcher.Triggered += ShowOverlay;
        if (config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0)
        {
            _gamepad.Start();
        }

        ApplyGestures(config.Gestures);

        if (_monitor is not null)
        {
            _monitor.HomeAppExited += OnHomeAppExited;
        }
    }

    public void ApplyGestures(GestureConfig gestures)
    {
        if (gestures.BottomEdge && _bottomStrip is null)
        {
            _bottomStrip = new EdgeSwipeWindow(ScreenEdge.Bottom, gestures.StripThickness);
            _bottomStrip.Triggered += ShowOverlay;
            _bottomStrip.Show();
        }
        else if (!gestures.BottomEdge && _bottomStrip is not null)
        {
            _bottomStrip.Close();
            _bottomStrip = null;
        }

        if (gestures.RightEdge && _rightStrip is null)
        {
            _rightStrip = new EdgeSwipeWindow(ScreenEdge.Right, gestures.StripThickness);
            _rightStrip.Triggered += ShowOverlay;
            _rightStrip.Show();
        }
        else if (!gestures.RightEdge && _rightStrip is not null)
        {
            _rightStrip.Close();
            _rightStrip = null;
        }
    }

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
        Log.Info("Config reloaded.");
    }

    private void OnHomeAppExited()
    {
        if (_config.HomeApp.AutoRelaunch)
        {
            Log.Info("Home app exited — auto-relaunching in 10 s.");
            System.Threading.Tasks.Task.Delay(10_000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(StartOrFocusHomeApp));
            return;
        }
        ShowOverlay();
    }

    public void ShowOverlay()
    {
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
            HomeAppName = System.IO.Path.GetFileNameWithoutExtension(_config.HomeApp.Path is { Length: > 0 } p ? p : "Home app"),
            GlyphStyle = _config.GlyphStyle,
            WarningText = _pendingWarning,
        };

        _overlayViewModel = vm;
        _overlay = new OverlayWindow(vm);
        _overlay.HomeAppRequested += () => { CloseOverlay(); StartOrFocusHomeApp(); };
        _overlay.DesktopRequested += () =>
        {
            var explorerRunning = ExplorerControl.IsRunningInSession();
            CloseOverlay();
            if (explorerRunning)
            {
                ExplorerControl.KillExplorer();
                StartOrFocusHomeApp();
            }
            else
            {
                ExplorerControl.StartExplorer();
            }
        };
        _overlay.Dismissed += () => { CloseOverlay(); FocusHomeApp(); };
        _overlay.Closed += (_, _) =>
        {
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
            if (reopenForWarning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowOverlay);
            }
        };

        _navigation = new GamepadNavigation(_gamepad, _overlay, () => { CloseOverlay(); FocusHomeApp(); },
            isNintendoLayout: () => _config.GlyphStyle == GlyphStyle.Nintendo);
        _gamepad.Start();
        _overlay.Show();
        _overlay.Activate();
    }

    private void CloseOverlay()
    {
        _pendingWarning = "";
        _reopenOverlayForWarning = false;
        _overlay?.Close();
    }

    private void StartOrFocusHomeApp()
    {
        if (_monitor?.IsAlive == true)
        {
            FocusHomeApp();
            return;
        }
        var home = _config.HomeApp;
        if (string.IsNullOrWhiteSpace(home.Path))
        {
            return;
        }

        if (!TryBeginHomeLaunch())
        {
            return;
        }

        try
        {
            var result = AppLauncher.Start(home.Path, home.Args, home.Elevated);
            if (!result.Started)
            {
                SetWarning($"Couldn't start {System.IO.Path.GetFileNameWithoutExtension(home.Path)}. Check its path and permissions.");
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
            else if (result.ElevationDeclined)
            {
                SetWarning("Home app started WITHOUT elevation (UAC declined).");
            }
        }
        finally
        {
            EndHomeLaunch();
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

    private void FocusHomeApp()
    {
        var home = _config.HomeApp;
        if (_monitor?.IsAlive != true)
        {
            return;
        }
        // Protocol re-activation self-focuses even against an elevated target (UIPI-proof).
        if (!string.IsNullOrWhiteSpace(home.ActivationProtocol))
        {
            AppLauncher.StartProtocol(home.ActivationProtocol);
            return;
        }
        var hwnd = WindowFinder.FindWindow(home.ProcessNames, home.WindowClass);
        WindowFinder.BringToForeground(hwnd);
    }

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.HomeAppExited -= OnHomeAppExited;
        }
        _hotkey.Dispose();
        _chordWatcher.Dispose();
        _gamepad.Dispose();
        _bottomStrip?.Close();
        _rightStrip?.Close();
        _overlay?.Close();
    }
}
