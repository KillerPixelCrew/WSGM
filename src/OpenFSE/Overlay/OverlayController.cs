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
    private readonly AppConfig _config;
    private readonly HomeAppMonitor? _monitor;
    private readonly HotkeyService _hotkey;
    private readonly GamepadService _gamepad = new();
    private EdgeSwipeWindow? _bottomStrip;
    private EdgeSwipeWindow? _rightStrip;
    private OverlayWindow? _overlay;
    private GamepadNavigation? _navigation;
    private string _pendingWarning = "";

    public OverlayController(AppConfig config, HomeAppMonitor? monitor)
    {
        _config = config;
        _monitor = monitor;

        _hotkey = new HotkeyService(MessageWindow.Create());
        _hotkey.Pressed += ShowOverlay;
        _hotkey.Apply(config.Hotkey);

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

    public void SetWarning(string warning) => _pendingWarning = warning;

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
            _navigation?.Dispose();
            _navigation = null;
            _gamepad.Stop();
            _overlay = null;
        };

        _navigation = new GamepadNavigation(_gamepad, _overlay, () => { CloseOverlay(); FocusHomeApp(); },
            nintendoLayout: _config.GlyphStyle == GlyphStyle.Nintendo);
        _gamepad.Start();
        _overlay.Show();
        _overlay.Activate();
    }

    private void CloseOverlay()
    {
        _pendingWarning = "";
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
        var result = AppLauncher.Start(home.Path, home.Args, home.Elevated);
        if (result.ElevationDeclined)
        {
            SetWarning("Home app started WITHOUT elevation (UAC declined).");
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
        _hotkey.Dispose();
        _gamepad.Dispose();
        _bottomStrip?.Close();
        _rightStrip?.Close();
        _overlay?.Close();
    }
}
