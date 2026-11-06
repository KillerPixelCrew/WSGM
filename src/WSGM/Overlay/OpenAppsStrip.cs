using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace WSGM.Overlay;

/// <summary>
///     The input of an Open apps strip, shared by the sheet's rail and the Game Mode taskbar: window chip and tray
///     presses, right-click tray menus and keeping controller focus in view. The chips and tray pills are the
///     <c>DeckAppChipTemplate</c> and <c>DeckTrayIconTemplate</c> templates in CommandDeck.axaml.
/// </summary>
internal static class OpenAppsStrip
{
    /// <summary>Routes a strip's presses to its window's events.</summary>
    /// <param name="apps">The scroller holding the window chips.</param>
    /// <param name="tray">The scroller holding the tray pills.</param>
    /// <param name="pick">Activates a window chip's window.</param>
    /// <param name="trayActivated">
    ///     Forwards a tray press: the icon, whether its context menu was asked for, and the pill it came from.
    /// </param>
    /// <param name="close">Asks a chip's window to close, for a strip with Close actions; null otherwise.</param>
    internal static void Attach(ScrollViewer apps, ScrollViewer tray, Action<AppSwitcherEntry> pick,
        Action<TrayIconEntry, bool, Control> trayActivated, Action<AppSwitcherEntry>? close = null)
    {
        apps.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not Button { DataContext: AppSwitcherEntry entry } button)
            {
                return;
            }

            if (button.Classes.Contains("window-close"))
            {
                close?.Invoke(entry);
            }
            else
            {
                pick(entry);
            }
        });
        // Tap, A or left click: the icon's primary activation.
        tray.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: TrayIconEntry entry } button)
            {
                trayActivated(entry, false, button);
            }
        });
        // Right click: the context menu many tray apps only answer. Button.Click never fires for that button.
        tray.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right
                || (e.Source as Visual)?.FindAncestorOfType<Button>(true) is not
                { DataContext: TrayIconEntry entry } button)
            {
                return;
            }

            e.Handled = true;
            trayActivated(entry, true, button);
        }, RoutingStrategies.Bubble, true);
        apps.AddHandler(InputElement.GotFocusEvent, BringFocusedIntoView, RoutingStrategies.Bubble);
        tray.AddHandler(InputElement.GotFocusEvent, BringFocusedIntoView, RoutingStrategies.Bubble);
    }

    /// <summary>
    ///     Scrolls a newly focused element into its scroller's viewport. Directional focus does not raise
    ///     RequestBringIntoView on its own, so a chip scrolled out of view would otherwise take focus invisibly.
    /// </summary>
    /// <param name="sender">The scroller the handler is attached to.</param>
    /// <param name="e">The focus change; the scroller itself is never the target.</param>
    internal static void BringFocusedIntoView(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>The screen point just below a pill's centre, where a tray app anchors a menu under the sheet's header.</summary>
    /// <param name="control">The pill.</param>
    /// <returns>The physical anchor point.</returns>
    internal static PixelPoint AnchorBelow(Control control)
    {
        return control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height));
    }

    /// <summary>The screen point just above a pill's centre, where a tray app anchors a menu over the taskbar.</summary>
    /// <param name="control">The pill.</param>
    /// <returns>The physical anchor point.</returns>
    internal static PixelPoint AnchorAbove(Control control)
    {
        return control.PointToScreen(new Point(control.Bounds.Width / 2, 0));
    }
}
