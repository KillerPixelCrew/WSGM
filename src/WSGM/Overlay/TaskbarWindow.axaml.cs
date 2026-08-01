using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WSGM.Overlay;

/// <summary>The game-mode taskbar: a thin, centered, bottom-docked icon strip of
/// the switchable windows (and, when the tray host is live, tray icons). Shares the
/// overlay's focus-taking model: safe only because the Steam Input pin keeps the
/// pad readable while a WSGM window is foreground.</summary>
public partial class TaskbarWindow : Window
{
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;

    /// <summary>Raised when the user picks an application tile.</summary>
    public event Action<TaskbarEntry>? WindowPicked;

    /// <summary>Raised when the taskbar is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>The control gamepad navigation should land on when the bar opens:
    /// the first application tile (null before the ItemsControl materializes).</summary>
    internal InputElement? DefaultFocusTarget => FindFirstTile();

    /// <summary>Creates the taskbar window bound to the supplied state.</summary>
    /// <param name="viewModel">The tile collection driving the strip.</param>
    public TaskbarWindow(TaskbarViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) => StopSlide();
        // SizeToContent: the strip grows/shrinks as the 1 s refresh adds or drops
        // tiles — keep it centered on the bottom edge (skip during the slide-in,
        // which owns Position until it finishes).
        SizeChanged += (_, _) =>
        {
            if (_slideTimer is null && IsVisible)
            {
                SnapToBottomCenter();
            }
        };

        // Same touch-promotion defense as OverlayWindow (CLAUDE.md invariant 3):
        // Avalonia never handles raw touch, DefWindowProc promotes the tap into a
        // synthesized mouse click delivered late; eat it here, and let the
        // controller's deferred Close() keep this window alive to do so.
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
        DockToBottomEdge();
        FindFirstTile()?.Focus(NavigationMethod.Directional);
    }

    private InputElement? FindFirstTile()
    {
        foreach (var descendant in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this))
        {
            if (descendant is Button { IsEffectivelyVisible: true } button)
            {
                return button;
            }
        }
        return null;
    }

    private PixelPoint ComputeBottomCenter()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return Position;
        }
        var bounds = screen.Bounds;
        var scaling = screen.Scaling;
        var widthPx = (int)Math.Ceiling(Width * scaling);
        var heightPx = (int)Math.Ceiling(Height * scaling);
        return new PixelPoint(
            bounds.X + Math.Max(0, (bounds.Width - widthPx) / 2),
            bounds.Y + bounds.Height - heightPx);
    }

    private void SnapToBottomCenter() => Position = ComputeBottomCenter();

    /// <summary>Centers the bar over the bottom edge of the primary display and
    /// slides it up from below the screen (mirror of the overlay's right-edge
    /// slide-in).</summary>
    private void DockToBottomEdge()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        _slideEnd = ComputeBottomCenter();
        _slideStart = new PixelPoint(_slideEnd.X, screen.Bounds.Y + screen.Bounds.Height);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        // Parameterless ctor + explicit Start (CLAUDE.md invariant 4).
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            _slideEnd.X,
            (int)Math.Round(_slideStart.Y + (_slideEnd.Y - _slideStart.Y) * eased));

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

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TaskbarEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }
}
