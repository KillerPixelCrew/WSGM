using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
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
}
