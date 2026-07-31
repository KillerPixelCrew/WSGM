using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenFSE.Overlay;

public partial class OverlayWindow : Window
{
    private bool _confirmRestart;
    private bool _confirmShutdown;
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;

    public event Action? HomeAppRequested;
    public event Action? DesktopRequested;
    public event Action? SettingsRequested;
    public event Action? ExitBigPictureRequested;
    public event Action? CloseLauncherRequested;
    public event Action? CycleAppsRequested;
    public event Action? Dismissed;

    private bool _confirmCloseLauncher;

    /// <summary>The control gamepad navigation should land on when the panel opens
    /// or when focus tracking is lost.</summary>
    internal InputElement DefaultFocusTarget => HomeAppButton;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) => StopSlide();

        // The overlay takes focus Game-Bar-style: the game stops receiving input
        // while the panel is open. Viable because SteamInputPin keeps the pad
        // readable even with a non-game window focused.
        //
        // Touch pass-through defense: Avalonia never marks touch raw events
        // handled, so WM_POINTER falls to DefWindowProc, which PROMOTES a tap into
        // a synthesized mouse click delivered AFTER the tap's dispatch. The
        // synthesized-message eater in WndProcHook consumes it — as long as this
        // window still exists when it arrives, which is why OverlayController
        // defers Close() by a beat. (The clean fix — consuming the raw touch
        // event — needs Avalonia's [PrivateApi] InputManager, which is stripped
        // from the published reference assemblies.)
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Eat touch/pen-SYNTHESIZED mouse messages (MI_WP_SIGNATURE). They are
        // promotion ghosts — the tap itself already went through the WM_POINTER
        // pipeline — and around the deferred close they would otherwise double-fire
        // or leak to the window underneath. Real mouse messages pass untouched.
        if (msg is Interop.NativeMethods.WmMouseMove
                or Interop.NativeMethods.WmLButtonDown
                or Interop.NativeMethods.WmLButtonUp)
        {
            var extra = (uint)Interop.NativeMethods.GetMessageExtraInfo();
            if ((extra & Interop.NativeMethods.MiWpSignatureMask) == Interop.NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        DockToRightEdge();
        HomeAppButton.Focus(NavigationMethod.Directional);
    }

    /// <summary>Fits the panel to the primary display and slides it in from the right.
    /// The window never covers the whole display, so the active game remains visible.</summary>
    private void DockToRightEdge()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var scaling = screen.Scaling;
        // Keep the panel compact on high-DPI handheld displays. A 360-DIP panel
        // remains comfortably touchable without taking half the game view.
        var panelWidth = Math.Min(360d, Math.Max(320d, bounds.Width / scaling * 0.30));
        Width = panelWidth;
        Height = bounds.Height / scaling;

        var panelWidthPx = (int)Math.Ceiling(panelWidth * scaling);
        _slideEnd = new PixelPoint(bounds.X + bounds.Width - panelWidthPx, bounds.Y);
        _slideStart = new PixelPoint(bounds.X + bounds.Width, bounds.Y);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        // Cubic ease-out keeps the movement quick without a sharp stop.
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            (int)Math.Round(_slideStart.X + (_slideEnd.X - _slideStart.X) * eased),
            _slideEnd.Y);

        if (progress >= 1)
        {
            StopSlide();
        }
    }

    private void StopSlide()
    {
        if (_slideTimer is null)
        {
            return;
        }
        _slideTimer.Stop();
        _slideTimer.Tick -= OnSlideTick;
        _slideTimer = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismissed?.Invoke();
        }
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only dismiss when the press hit the backdrop itself, not a button.
        if (ReferenceEquals(e.Source, sender))
        {
            Dismissed?.Invoke();
        }
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e) => HomeAppRequested?.Invoke();
    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();
    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnExitBigPicture(object? sender, RoutedEventArgs e) => ExitBigPictureRequested?.Invoke();
    private void OnCycleApps(object? sender, RoutedEventArgs e) => CycleAppsRequested?.Invoke();
    private void OnClose(object? sender, RoutedEventArgs e) => Dismissed?.Invoke();

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (!_confirmCloseLauncher)
        {
            _confirmCloseLauncher = true;
            CloseLauncherTitle.Text = "Really?";
            return;
        }
        _confirmCloseLauncher = false;
        CloseLauncherRequested?.Invoke();
    }

    private void OnSleep(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Sleep();
    }

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (!_confirmRestart)
        {
            _confirmRestart = true;
            RestartButton.Content = "Really?";
            return;
        }
        Core.PowerActions.Restart();
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!_confirmShutdown)
        {
            _confirmShutdown = true;
            ShutdownButton.Content = "Really?";
            return;
        }
        Core.PowerActions.Shutdown();
    }
}
