using System;
using Avalonia;
using Avalonia.Controls;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>A top-center, non-activating and click-through volume OSD. It never
/// takes focus from the game and is constructed only when Windows reports no
/// exclusive Direct3D fullscreen session.</summary>
public partial class VolumeIndicatorWindow : Window
{
    private readonly double _uiScale;

    /// <summary>Creates the indicator at the caller's game-mode UI scale.</summary>
    /// <param name="uiScale">The desired desktop-DPI scale factor.</param>
    public VolumeIndicatorWindow(double uiScale)
    {
        _uiScale = uiScale;
        InitializeComponent();
        Opened += (_, _) => PositionAndMakeClickThrough();
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    /// <summary>Updates the visual state for the current default-endpoint volume.</summary>
    /// <param name="percentage">The master volume in the inclusive 0–100 range.</param>
    /// <param name="muted">Whether the endpoint is currently muted.</param>
    public void Update(int percentage, bool muted)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        VolumeLevel.Value = percentage;
        VolumePercent.Text = $"{percentage}%";
        VolumeTitle.Text = muted ? "Muted" : "Volume";
        VolumeIcon.Text = muted || percentage == 0
            ? "🔇"
            : percentage < 34 ? "🔈" : percentage < 67 ? "🔉" : "🔊";
    }

    private void PositionAndMakeClickThrough()
    {
        var screen = Screens?.Primary;
        if (screen is not null)
        {
            var factor = Math.Clamp(_uiScale / screen.Scaling, 1.0, 3.0);
            Width = 320 * factor;
            Height = 106 * factor;
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            var y = screen.Bounds.Y + Math.Max(24, (int)Math.Round(screen.Bounds.Height * 0.08));
            Position = new PixelPoint(
                screen.Bounds.X + Math.Max(0, (screen.Bounds.Width - width) / 2), y);
        }

        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd != 0)
        {
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle,
                style | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent);
        }
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmNcHitTest)
        {
            handled = true;
            return (IntPtr)NativeMethods.HtTransparent;
        }
        return IntPtr.Zero;
    }
}
