using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace WSGM.Shell;

/// <summary>The borderless startup splash shown while Steam Big Picture launches.</summary>
public partial class BootSplashWindow : Window
{
    /// <summary>Raised when the user chooses the desktop fallback.</summary>
    public event Action? DesktopRequested;

    /// <summary>The control gamepad navigation should land on for the first D-pad
    /// press. Nothing is focused on open, so a stray A press activates nothing.</summary>
    internal InputElement DefaultFocusTarget => DesktopButton;

    private readonly RotateTransform _spinnerRotate = new();
    private DispatcherTimer? _spinnerTimer;
    private DispatcherTimer? _fadeTimer;
    private DateTime _spinnerStartedUtc;
    private DateTime _fadeStartedUtc;
    private TimeSpan _fadeDuration;
    private Action? _fadeDone;
    private nint _hwnd;

    /// <summary>Creates the splash window and its controller navigation.</summary>
    public BootSplashWindow()
    {
        InitializeComponent();
        // Non-control objects don't get x:Name codegen fields — wire in code.
        Spinner.RenderTransform = _spinnerRotate;
        Opened += OnOpened;
        Closed += (_, _) => StopTimers();

        // Touch pass-through defense, same as OverlayWindow: Avalonia never marks
        // touch raw events handled, so WM_POINTER falls to DefWindowProc, which
        // promotes a tap into a delayed synthesized mouse click. Eat those here so
        // a splash tap can never land on whatever the splash was covering.
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
        CoverPrimaryScreen();
        // Service boots apply the 100% game-mode scale while the splash is already
        // up (the cover must precede the posture change) — re-cover so the DPI
        // change can't leave desktop pixels exposed around a stale-sized splash.
        if (Screens is not null)
        {
            Screens.Changed += OnScreensChanged;
            Closed += (_, _) => Screens.Changed -= OnScreensChanged;
        }

        // Layered style applied once, fully opaque — flipping it mid-fade risks a
        // first-frame flicker. The fade is cosmetic; without an HWND it is skipped.
        _hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (_hwnd != 0)
        {
            var ex = Interop.NativeMethods.GetWindowLong(_hwnd, Interop.NativeMethods.GwlExStyle);
            Interop.NativeMethods.SetWindowLong(_hwnd, Interop.NativeMethods.GwlExStyle,
                ex | Interop.NativeMethods.WsExLayered);
            Interop.NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, Interop.NativeMethods.LwaAlpha);
        }

        _spinnerStartedUtc = DateTime.UtcNow;
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick += OnSpinnerTick;
        _spinnerTimer.Start();
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        // Time-based (one revolution per second) so a busy UI thread can't slow it.
        var elapsed = (DateTime.UtcNow - _spinnerStartedUtc).TotalMilliseconds;
        _spinnerRotate.Angle = elapsed * 0.36 % 360;
    }

    /// <summary>Fades the whole window (layered alpha) over what's underneath, then
    /// invokes <paramref name="onDone"/>. Degrades to an immediate callback when the
    /// platform handle is unavailable.</summary>
    public void BeginFadeOut(TimeSpan duration, Action onDone)
    {
        if (_hwnd == 0 || _fadeTimer is not null)
        {
            onDone();
            return;
        }
        _fadeDuration = duration;
        _fadeDone = onDone;
        _fadeStartedUtc = DateTime.UtcNow;
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += OnFadeTick;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(
            (DateTime.UtcNow - _fadeStartedUtc).TotalMilliseconds / _fadeDuration.TotalMilliseconds, 0, 1);
        Interop.NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, (byte)Math.Round(255 * (1 - progress)), Interop.NativeMethods.LwaAlpha);
        if (progress >= 1)
        {
            _fadeTimer?.Stop();
            var done = _fadeDone;
            _fadeDone = null;
            done?.Invoke();
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        CoverPrimaryScreen();
        Core.Log.Info("Boot splash resized after display change.");
    }

    /// <summary>Primary display only — same assumption as the overlay; startup apps
    /// on a secondary screen may still flash (accepted on single-screen handhelds).</summary>
    private void CoverPrimaryScreen()
    {
        var screen = Screens?.Primary ?? (Screens?.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var bounds = screen.Bounds;
        // Window scaling, not screen.Scaling — the screens cache is stale after a
        // runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var scaling = DesktopScaling;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
    }

    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();

    private void StopTimers()
    {
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _fadeDone = null;
    }
}
