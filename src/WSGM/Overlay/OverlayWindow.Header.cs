using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     Share of the header's inner width the tray strip may claim before it
    ///     starts scrolling. The tray is the only header content whose length WSGM does
    ///     not control (the Shell_TrayWnd host takes whatever apps register), so it is
    ///     the part that gets a budget; the wordmark and the status pills after it are
    ///     fixed-size and always keep their space.
    /// </summary>
    private const double TrayWidthFraction = 0.30;

    /// <summary>
    ///     Floor for the tray budget: one tray pill plus its spacing, so a
    ///     single icon is never clipped even on an absurdly narrow display.
    /// </summary>
    private const double TrayMinWidth = 40;

    /// <summary>
    ///     Horizontal padding the header adds inside the window (16 left + 16
    ///     right — keep in sync with Padding="16,0" in the XAML).
    /// </summary>
    private const double HeaderHorizontalPadding = 32;

    /// <summary>
    ///     The header's bottom edge in physical screen pixels, for the radio,
    ///     audio and eject panels to hang from.
    /// </summary>
    internal int HeaderBottomScreenY
        => Position.Y
           + (int)Math.Ceiling(
               Header.Bounds.Height * _contentScale * StatusPanel.CurrentWindowScale(this));

    /// <summary>The sheet's physical right edge, used to keep peer panels on the same display.</summary>
    internal int RightScreenX
        => Position.X + (int)Math.Ceiling(Bounds.Width * StatusPanel.CurrentWindowScale(this));

    /// <summary>
    ///     Lands controller focus on the first Open apps chip — the bottom-swipe
    ///     entry point, which exists to reach the running programs in one gesture.
    /// </summary>
    internal void FocusOpenApps()
    {
        FocusSearch.FirstNavigable(AppTiles)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Y: switch to the window after the foreground one in strip order,
    ///     wrapping — an Alt+Tab step from the sheet. Nothing to do with fewer than two
    ///     windows.
    /// </summary>
    internal void CycleNextApp()
    {
        var entries = _switcher.Entries;
        if (entries.Count < 2)
        {
            Log.Info("Next app: fewer than two windows open.");
            return;
        }

        var active = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsActive)
            {
                continue;
            }

            active = i;
            break;
        }

        WindowPicked?.Invoke(entries[(active + 1) % entries.Count]);
    }

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppSwitcherEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }

    /// <summary>Tap / A-button / left click → the icon's primary activation.</summary>
    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrayIconEntry entry } control)
        {
            TrayIconActivated?.Invoke(entry, false, AnchorBelow(control));
        }
    }

    /// <summary>
    ///     Right mouse button → the icon's context menu (many tray apps only
    ///     respond to this). Button.Click never fires for the right button, so this
    ///     rides PointerReleased.
    /// </summary>
    private void OnTrayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Control { DataContext: TrayIconEntry entry } control)
        {
            return;
        }

        e.Handled = true;
        TrayIconActivated?.Invoke(entry, true, AnchorBelow(control));
    }

    /// <summary>
    ///     Screen position just below the pill's centre — where the app
    ///     should anchor a popup menu (v4 coordinate protocol).
    /// </summary>
    private static PixelPoint AnchorBelow(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height));
        return new PixelPoint(point.X, point.Y);
    }

    private void OnRadioTileClicked(object? sender, RoutedEventArgs e)
    {
        // A flyout cannot hold a network list, and GamepadNavigation has no popup
        // awareness, so a list inside one would not be reachable with a controller
        // at all. The panel is a real window for both reasons.
        RadioPanelRequested?.Invoke((sender as Control)?.Tag as string == "bluetooth");
    }

    private void OnAudioTileClicked(object? sender, RoutedEventArgs e)
    {
        AudioPanelRequested?.Invoke();
    }

    private void OnEjectTileClicked(object? sender, RoutedEventArgs e)
    {
        EjectPanelRequested?.Invoke();
    }

    /// <summary>
    ///     Scrolls a newly focused chip into its strip's viewport (app chips
    ///     and tray icons share this handler). Bubbles from the buttons; the scroll
    ///     viewers themselves are not focusable, and the call is a no-op when the chip
    ///     is already fully visible.
    /// </summary>
    private static void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>
    ///     The widest the tray strip may become before it scrolls, so that
    ///     the fixed status pills always fit. Pure: the width budget is unit-tested
    ///     against this method rather than against a live window.
    /// </summary>
    /// <param name="windowWidth">The sheet window's logical (DIP) width.</param>
    /// <param name="contentScale">
    ///     The factor RootScale applies to the content
    ///     (see <see cref="DockToTopEdge" />); 1.0 when untransformed.
    /// </param>
    /// <returns>A MaxWidth in the header's pre-transform layout units.</returns>
    internal static double ComputeTrayMaxWidth(double windowWidth, double contentScale)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(contentScale) || contentScale <= 0)
        {
            return TrayMinWidth;
        }

        var inner = windowWidth / contentScale - HeaderHorizontalPadding;
        return Math.Max(TrayMinWidth, inner * TrayWidthFraction);
    }

    /// <summary>
    ///     Spans the sheet across the summoning window's display top edge, sized to
    ///     <see cref="SheetHeightFraction" /> of its height, and slides it down from
    ///     above the screen. The window never covers the whole display: the strip left
    ///     below is the game's, and the tap-outside dismissal.
    /// </summary>
    private void DockToTopEdge()
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
        var factor = Math.Clamp(_uiScale / scaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Quick access UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            _contentScale = factor;
            RootScale.LayoutTransform = new ScaleTransform(factor, factor);
        }

        Width = bounds.Width / scaling;
        Height = Math.Round(bounds.Height / scaling * SheetHeightFraction);
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!TryCancelSubView())
        {
            Dismissed?.Invoke();
        }

        e.Handled = true;
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e)
    {
        HomeAppRequested?.Invoke();
    }

    private void OnDesktop(object? sender, RoutedEventArgs e)
    {
        DesktopRequested?.Invoke();
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnExitBigPicture(object? sender, RoutedEventArgs e)
    {
        ExitBigPictureRequested?.Invoke();
    }

    private void OnTaskManager(object? sender, RoutedEventArgs e)
    {
        TaskManagerRequested?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
    }
}
