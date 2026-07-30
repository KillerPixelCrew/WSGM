using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using OpenFSE.Interop;

namespace OpenFSE.Overlay;

public enum ScreenEdge
{
    Bottom,
    Right,
}

/// <summary>Invisible, topmost, non-activating strip along a screen edge that detects
/// an inward swipe (touch or mouse drag) and raises Triggered.</summary>
public sealed class EdgeSwipeWindow : Window
{
    private readonly ScreenEdge _edge;
    private readonly int _thicknessPx;
    private Point _start;
    private ulong _t0;
    private bool _tracking;

    private const double TriggerDistanceDip = 30;
    private const ulong TriggerTimeMs = 500;

    public event Action? Triggered;

    public EdgeSwipeWindow(ScreenEdge edge, int thicknessPx)
    {
        _edge = edge;
        _thicknessPx = Math.Max(8, thicknessPx);

        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Focusable = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        // Alpha 1/255: effectively invisible but still hit-testable (fully
        // transparent surfaces would be click-through).
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        Opened += (_, _) =>
        {
            ApplyNoActivate();
            Reposition();
            if (Screens is not null)
            {
                Screens.Changed += (_, _) => Reposition();
            }
        };
    }

    private void ApplyNoActivate()
    {
        if (TryGetPlatformHandle() is { } handle)
        {
            var exStyle = NativeMethods.GetWindowLongW(handle.Handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongW(handle.Handle, NativeMethods.GwlExStyle,
                exStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
        }
    }

    private void Reposition()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;        // physical pixels
        var scaling = screen.Scaling;

        if (_edge == ScreenEdge.Bottom)
        {
            Position = new PixelPoint(bounds.X, bounds.Y + bounds.Height - _thicknessPx);
            Width = bounds.Width / scaling;
            Height = _thicknessPx / scaling;
        }
        else
        {
            Position = new PixelPoint(bounds.X + bounds.Width - _thicknessPx, bounds.Y);
            Width = _thicknessPx / scaling;
            Height = bounds.Height / scaling;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _tracking = true;
        _start = e.GetPosition(this);
        _t0 = e.Timestamp;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_tracking)
        {
            return;
        }
        var p = e.GetPosition(this);
        var inward = _edge == ScreenEdge.Bottom ? _start.Y - p.Y : _start.X - p.X;
        if (inward > TriggerDistanceDip && e.Timestamp - _t0 < TriggerTimeMs)
        {
            _tracking = false;
            e.Pointer.Capture(null);
            Triggered?.Invoke();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _tracking = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _tracking = false;
    }
}
