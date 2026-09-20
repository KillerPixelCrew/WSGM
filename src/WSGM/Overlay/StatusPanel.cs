using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>Shared focus scrolling and effective display scaling for overlay surfaces.</summary>
internal static class StatusPanel
{
    /// <summary>Keeps a utility panel's focused control in its scrolling viewport.</summary>
    internal static void WirePanelBehaviour(Control scroller)
    {
        scroller.AddHandler(InputElement.GotFocusEvent, OnRowGotFocus, RoutingStrategies.Bubble);
    }

    private static void OnRowGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>
    ///     Gets the HWND's current effective scale, falling back to Avalonia only when the
    ///     native handle is unavailable. All in-window surfaces inherit the sheet's scale.
    /// </summary>
    internal static double CurrentWindowScale(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        var dpi = hwnd == 0 ? 0 : NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? window.DesktopScaling : dpi / 96.0;
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }
}
