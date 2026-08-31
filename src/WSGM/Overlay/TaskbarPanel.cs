using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
/// The window mechanics the three panels that dock above the game-mode taskbar share: radio, audio
/// and safe eject. Each owns its own content and commands; only the geometry is common.
/// </summary>
internal static class TaskbarPanel
{
    /// <summary>Renders the panel at the user's desktop DPI, clamps it to the space above the bar,
    /// and parks it against the bar's right end. Game mode forces every display to 100% scaling,
    /// which would otherwise shrink a DIP-sized panel — and any on-screen keyboard inside it — to
    /// millimetres on a dense handheld display.</summary>
    /// <param name="window">The panel window being positioned.</param>
    /// <param name="root">The panel's layout-transform root, which carries the touch scale.</param>
    /// <param name="uiScale">The configured overlay UI scale.</param>
    /// <param name="baseWidth">The panel's design width in DIPs.</param>
    /// <param name="baseHeight">The panel's design height in DIPs.</param>
    /// <param name="taskbarTop">The taskbar's physical top edge, or 0 when it is not showing.</param>
    /// <param name="name">Panel name for the scale log line.</param>
    /// <remarks>
    /// Positioned from the bar's ACTUAL top edge rather than derived from the working area: the bar
    /// is a topmost window, not a registered appbar, so the working area does not account for it and
    /// deriving the position from screen height minus bar height double-counted, leaving a visible
    /// gap. The scale factor comes from the window, never <c>screen.Scaling</c> — the screens cache
    /// still reports the pre-game-mode factor at exactly the moment this runs, and using it parked
    /// the panel far from the bar (device-reported).
    /// </remarks>
    internal static void DockAboveTaskbar(
        Window window,
        LayoutTransformControl root,
        double uiScale,
        double baseWidth,
        double baseHeight,
        int taskbarTop,
        string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);

        double scale = window.DesktopScaling;
        double factor = Math.Clamp(uiScale / scale, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"{name} panel UI scale {factor:0.##}x (desktop DPI over current {scale:0.##}).");
            root.LayoutTransform = new ScaleTransform(factor, factor);
        }

        Screen? screen = window.Screens.Primary
            ?? (window.Screens.ScreenCount > 0 ? window.Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.Bounds;
        int bottom = taskbarTop > 0 ? taskbarTop : area.Y + area.Height;

        // Clamp against the space above the bar, in DIPs. The panel's own scroll viewer absorbs a
        // shortened panel, and the sizes must be final before the position is computed from them.
        window.Width = Math.Min(baseWidth * factor, (area.Width / scale) - 12);
        window.Height = Math.Min(baseHeight * factor, ((bottom - area.Y) / scale) - 8);
        window.UpdateLayout();

        int width = (int)Math.Round(window.Width * scale);
        int height = (int)Math.Round(window.Height * scale);
        // Small and deliberate: the panel should look attached to the bar, not floating above it.
        int gap = (int)Math.Round(2 * scale);
        int margin = (int)Math.Round(6 * scale);
        // Right-aligned, mirroring where the tiles are and where Windows puts its own quick
        // settings; never allowed to run off the top of a short display.
        int x = area.X + area.Width - width - margin;
        int y = Math.Max(area.Y, bottom - height - gap);
        window.Position = new PixelPoint(x, y);
    }
}
