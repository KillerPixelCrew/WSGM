using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LiveBackdrop;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>
///     Where and how the Quick access sheet sits on its display: the covered screen, the desktop-DPI
///     content scale, the slide-in from the top edge and the live glass backdrop behind the deck.
/// </summary>
internal sealed class OverlayPlacement
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

    private readonly Border _glassCanvas;
    private readonly PixelPoint? _preferredScreenPoint;
    private readonly LayoutTransformControl _rootScale;
    private readonly Control _tray;
    private readonly double _uiScale;
    private readonly OverlaySlide _slide;
    private readonly Window _window;
    private LiveBackdrop? _backdrop;
    private double _blurRadius = 8;
    private PixelPoint _slideEnd;
    private PixelPoint _slideStart;

    /// <summary>Creates the placement of one sheet window.</summary>
    /// <param name="window">The sheet window that is moved, sized and given the backdrop.</param>
    /// <param name="rootScale">The transform that lays the content out at the content scale.</param>
    /// <param name="glassCanvas">The canvas painted glass while the backdrop is live and opaque otherwise.</param>
    /// <param name="tray">The tray strip whose width is budgeted against the window.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (1.5 for a 150% desktop).</param>
    /// <param name="preferredScreenPoint">
    ///     A physical point in the foreground window that summoned the sheet, or null for Avalonia's
    ///     current window or primary-screen selection.
    /// </param>
    internal OverlayPlacement(Window window, LayoutTransformControl rootScale, Border glassCanvas, Control tray,
        double uiScale, PixelPoint? preferredScreenPoint)
    {
        _window = window;
        _rootScale = rootScale;
        _glassCanvas = glassCanvas;
        _tray = tray;
        _uiScale = uiScale;
        _preferredScreenPoint = preferredScreenPoint;
        // The sheet slides down from above the display.
        _slide = new OverlaySlide(progress => _window.Position = new PixelPoint(
            _slideEnd.X,
            (int)Math.Round(_slideStart.Y + (_slideEnd.Y - _slideStart.Y) * progress)));
        ApplyGlassTransparency();
        // Budget the tray against the XAML's declared width right away, so the strip is bounded even
        // on the path where Dock bails out (no primary screen); docking recomputes it against the
        // real display width.
        _tray.MaxWidth = ComputeTrayMaxWidth(window.Width, ContentScale);
    }

    /// <summary>
    ///     The factor the root transform currently applies (1.0 = no transform). The sheet's inner
    ///     layout happens in pre-transform units, so every budget derived from the window's width has
    ///     to divide by this first.
    /// </summary>
    internal double ContentScale { get; private set; } = 1.0;

    /// <summary>
    ///     The widest the tray strip may become before it scrolls, so that
    ///     the open-app actions always fit. Pure: the width budget is unit-tested
    ///     against this method rather than against a live window.
    /// </summary>
    /// <param name="windowWidth">The sheet window's logical (DIP) width.</param>
    /// <param name="contentScale">
    ///     The factor the root transform applies to the content (see <see cref="Dock" />); 1.0 when
    ///     untransformed.
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

    /// <summary>The content scale that renders the sheet at the desktop's DPI and still fits its display.</summary>
    /// <param name="uiScale">The saved desktop scale.</param>
    /// <param name="renderScale">The window's current render scaling.</param>
    /// <param name="width">The display's physical width.</param>
    /// <param name="height">The display's physical height.</param>
    /// <returns>The factor for <see cref="ApplyContentScale" />, never below the 980 x 640 DIP floor's fit.</returns>
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

    /// <summary>Lays the content out at <paramref name="factor" /> times its DIP size.</summary>
    /// <param name="factor">The content scale from <see cref="ComputeContentScale" />.</param>
    internal void ApplyContentScale(double factor)
    {
        ContentScale = factor;
        ScaleContent(_rootScale, factor);
    }

    /// <summary>Lays a surface's content out at <paramref name="factor" /> times its DIP size.</summary>
    /// <param name="rootScale">The surface's root transform.</param>
    /// <param name="factor">The content scale from <see cref="ComputeContentScale" />.</param>
    internal static void ScaleContent(LayoutTransformControl rootScale, double factor)
    {
        rootScale.LayoutTransform = new ScaleTransform(factor, factor);
    }

    /// <summary>
    ///     Covers the summoning window's display with a surface window and renders its content at the desktop's
    ///     DPI. Shared by the sheet and the Game Mode taskbar.
    /// </summary>
    /// <param name="window">The surface window, moved and sized to the display.</param>
    /// <param name="rootScale">The surface's root transform.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (1.5 for a 150% desktop).</param>
    /// <param name="preferredScreenPoint">A physical point on the display to cover, or null for the window's own.</param>
    /// <param name="surfaceName">The surface's name in the log.</param>
    /// <returns>The covered display, or null when there is none.</returns>
    internal static DisplayCover? Cover(Window window, LayoutTransformControl rootScale, double uiScale,
        PixelPoint? preferredScreenPoint, string surfaceName)
    {
        var screens = window.Screens;
        var screen = preferredScreenPoint is { } point
            ? screens.ScreenFromPoint(point)
            : null;
        screen ??= screens.ScreenFromWindow(window) ?? screens.Primary;
        if (screen is null && screens.ScreenCount > 0)
        {
            screen = screens.All[0];
        }

        if (screen is null)
        {
            return null;
        }

        var bounds = screen.Bounds;
        // A new top-level starts on Windows' default monitor. Move its HWND onto the
        // selected display before reading its effective DPI; otherwise a secondary
        // display is sized using the primary display's scale. Do not use
        // screen.Scaling: Avalonia's screen cache can retain the pre-game-mode DPI
        // when no window existed to receive the display transition.
        window.Position = new PixelPoint(bounds.X, bounds.Y);
        var scaling = StatusPanel.CurrentWindowScale(window);
        // Render at the desktop's DPI: game mode forces displays to 100%, which
        // otherwise shrinks a DIP-sized surface to millimeters on dense handheld
        // screens (device-reported). The content lays out in desktop-DIP space (the
        // factor divides the available size), the window takes the scaled-up
        // physical footprint.
        var factor = ComputeContentScale(uiScale, scaling, bounds.Width, bounds.Height);
        var applied = Math.Abs(factor - 1.0) >= 0.01;
        if (applied)
        {
            Log.Info($"{surfaceName} UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            ScaleContent(rootScale, factor);
        }

        window.Width = bounds.Width / scaling;
        window.Height = bounds.Height / scaling;
        return new DisplayCover(bounds, scaling, applied ? factor : 1.0);
    }

    /// <summary>
    ///     The GDI source name of the display the sheet is on, such as <c>\\.\DISPLAY2</c>, or null when
    ///     the window has no screen or Windows does not describe it.
    /// </summary>
    /// <returns>The Win32 display-source name for the window monitor, or null when the handle or monitor cannot be resolved.</returns>
    internal unsafe string? DisplaySourceName()
    {
        if (_window.Screens.ScreenFromWindow(_window)?.TryGetPlatformHandle()?.Handle is not { } monitor
            || monitor == 0)
        {
            return null;
        }

        var info = new NativeMethods.MonitorInfoExW { CbSize = (uint)sizeof(NativeMethods.MonitorInfoExW) };
        return NativeMethods.GetMonitorInfoW(monitor, ref info) ? new string((char*)info.Device) : null;
    }

    /// <summary>
    ///     Covers the summoning window's display and slides the live glass sheet into place.
    /// </summary>
    internal void Dock()
    {
        if (Cover(_window, _rootScale, _uiScale, _preferredScreenPoint, "Quick access") is not { } cover)
        {
            return;
        }

        ContentScale = cover.ContentScale;
        _tray.MaxWidth = ComputeTrayMaxWidth(_window.Width, ContentScale);
        var bounds = cover.Bounds;
        var heightPx = (int)Math.Ceiling(_window.Height * cover.RenderScale);
        _slideEnd = new PixelPoint(bounds.X, bounds.Y);
        _slideStart = new PixelPoint(bounds.X, bounds.Y - heightPx);
        _slide.Start();
    }

    /// <summary>Updates the attached backdrop without recreating the Overlay window.</summary>
    /// <param name="radius">Saved backdrop blur in physical pixels, normalized to the supported range before application.</param>
    internal void SetBlurRadius(double radius)
    {
        _blurRadius = double.IsFinite(radius) ? Math.Clamp(radius, 0, LiveBackdrop.MaximumBlurRadius) : 8;
        if (_backdrop is not null)
        {
            _backdrop.BlurRadius = _blurRadius;
        }
    }

    /// <summary>Attaches the live glass backdrop once the window is shown.</summary>
    internal void AttachBackdrop()
    {
        _backdrop = LiveBackdrop.Attach(_window, _blurRadius);
        _backdrop.StateChanged += OnBackdropChanged;
        OnBackdropChanged(_backdrop, EventArgs.Empty);
    }

    /// <summary>Stops the slide and releases the backdrop as the window closes.</summary>
    internal void Detach()
    {
        _slide.Stop();
        if (_backdrop is null)
        {
            return;
        }

        _backdrop.StateChanged -= OnBackdropChanged;
        _backdrop.Dispose();
        _backdrop = null;
    }

    private void OnBackdropChanged(object? sender, EventArgs e)
    {
        ApplyGlassTransparency();
        if (_backdrop?.IsActive == true)
        {
            Log.Debug($"Quick access live backdrop active (blur {_blurRadius:0.##} px).");
        }
        else if (_backdrop?.FailureReason is { } failure)
        {
            Log.Warn($"Quick access live backdrop unavailable: {failure}");
        }
        else if (_window.IsVisible)
        {
            Log.Info($"Quick access waiting for transparency ({_window.ActualTransparencyLevel}).");
        }
    }

    private void ApplyGlassTransparency()
    {
        var hasBackdrop = _backdrop?.IsActive == true;
        _glassCanvas.Background =
            _window.FindResource(hasBackdrop ? "DeckGlassCanvasBrush" : "DeckCanvasBrush") as IBrush;
    }

    /// <summary>The display a surface window covers.</summary>
    /// <param name="Bounds">The display's physical bounds.</param>
    /// <param name="RenderScale">The window's render scaling on that display.</param>
    /// <param name="ContentScale">The content scale applied, 1.0 when none was.</param>
    internal readonly record struct DisplayCover(PixelRect Bounds, double RenderScale, double ContentScale);
}
