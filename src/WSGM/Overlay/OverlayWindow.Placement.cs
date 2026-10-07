using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     Share of the bottom rail's inner width the tray may claim before scrolling,
    ///     leaving room for open apps and their actions.
    /// </summary>
    private const double TrayWidthFraction = 0.30;

    /// <summary>
    ///     Floor for the tray budget: one tray pill plus its spacing, so a
    ///     single icon is never clipped even on an absurdly narrow display.
    /// </summary>
    private const double TrayMinWidth = 40;

    /// <summary>
    ///     Horizontal padding of the bottom apps and tray rail.
    /// </summary>
    private const double RailHorizontalPadding = 32;

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Classes.Set("compact", e.NewSize.Width < 1150);
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(e.NewSize.Width, 1);
    }

    /// <summary>
    ///     The widest the tray strip may become before it scrolls, so that
    ///     the open-app actions always fit. Pure: the width budget is unit-tested
    ///     against this method rather than against a live window.
    /// </summary>
    /// <param name="windowWidth">The sheet window's logical (DIP) width.</param>
    /// <param name="contentScale">
    ///     The factor RootScale applies to the content
    ///     (see <see cref="DockToTopEdge" />); 1.0 when untransformed.
    /// </param>
    /// <returns>A MaxWidth in the rail's pre-transform layout units.</returns>
    internal static double ComputeTrayMaxWidth(double windowWidth, double contentScale)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(contentScale) || contentScale <= 0)
        {
            return TrayMinWidth;
        }

        var inner = windowWidth / contentScale - RailHorizontalPadding;
        return Math.Max(TrayMinWidth, inner * TrayWidthFraction);
    }

    /// <summary>Lays the content out at <paramref name="factor" /> times its DIP size.</summary>
    /// <param name="factor">The content scale from <see cref="ComputeContentScale" />.</param>
    internal void ApplyContentScale(double factor)
    {
        _contentScale = factor;
        RootScale.LayoutTransform = new ScaleTransform(factor, factor);
    }

    /// <summary>
    ///     The GDI source name of the display the sheet is on, such as <c>\\.\DISPLAY2</c>, or null when
    ///     the window has no screen or Windows does not describe it.
    /// </summary>
    /// <returns>The Win32 display-source name for the window monitor, or null when the handle or monitor cannot be resolved.</returns>
    internal unsafe string? DisplaySourceName()
    {
        if (Screens.ScreenFromWindow(this)?.TryGetPlatformHandle()?.Handle is not { } monitor || monitor == 0)
        {
            return null;
        }

        var info = new NativeMethods.MonitorInfoExW { CbSize = (uint)sizeof(NativeMethods.MonitorInfoExW) };
        return NativeMethods.GetMonitorInfoW(monitor, ref info) ? new string((char*)info.Device) : null;
    }

    /// <summary>
    ///     Covers the summoning window's display and slides the live glass sheet into place.
    /// </summary>
    internal void DockToTopEdge()
    {
        var screen = _preferredScreenPoint is { } point
            ? Screens.ScreenFromPoint(point)
            : null;
        screen ??= Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null && Screens.ScreenCount > 0)
        {
            screen = Screens.All[0];
        }

        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        // A new top-level starts on Windows' default monitor. Move its HWND onto the
        // selected display before reading its effective DPI; otherwise a secondary
        // display is sized using the primary display's scale. Do not use
        // screen.Scaling: Avalonia's screen cache can retain the pre-game-mode DPI
        // when no window existed to receive the display transition.
        Position = new PixelPoint(bounds.X, bounds.Y);
        var scaling = StatusPanel.CurrentWindowScale(this);
        // Render at the desktop's DPI: game mode forces displays to 100%, which
        // otherwise shrinks this DIP-sized sheet to millimeters on dense
        // handheld screens (device-reported). The content lays out in
        // desktop-DIP space (the factor divides the available size), the window
        // takes the scaled-up physical footprint.
        var factor = ComputeContentScale(_uiScale, scaling, bounds.Width, bounds.Height);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Quick access UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            ApplyContentScale(factor);
        }

        Width = bounds.Width / scaling;
        Height = Math.Round(bounds.Height / scaling);
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);

        var heightPx = (int)Math.Ceiling(Height * scaling);
        _slideEnd = new PixelPoint(bounds.X, bounds.Y);
        _slideStart = new PixelPoint(bounds.X, bounds.Y - heightPx);
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

    internal static double ComputeContentScale(double uiScale, double renderScale, double width, double height)
    {
        if (!double.IsFinite(uiScale) || !double.IsFinite(renderScale) || renderScale <= 0
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return 1;
        }

        var maximumFit = Math.Min(width / renderScale / 980, height / renderScale / 640);
        // Game Mode supplies the saved desktop scale while Windows runs at 100%. Desktop mode
        // supplies one because native DPI already scales the window. Only the viewport fit may
        // reduce that native factor; it never changes the saved preference.
        return Math.Min(Math.Clamp(uiScale / renderScale, 1.0, 3.0), maximumFit);
    }

    private void OnGlassBackdropChanged(object? sender, EventArgs e)
    {
        ApplyGlassTransparency();
    }

    private void ApplyGlassTransparency()
    {
        var hasBackdrop = _glassBackdrop?.IsActive == true;
        GlassCanvas.Background = this.FindResource(hasBackdrop ? "DeckGlassCanvasBrush" : "DeckCanvasBrush") as IBrush;
    }
}
