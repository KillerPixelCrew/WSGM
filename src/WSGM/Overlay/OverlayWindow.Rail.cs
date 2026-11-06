using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     Lands controller focus on the first Open apps chip, for the desktop's taskbar button, which
    ///     exists to reach the running programs in one gesture.
    /// </summary>
    internal void FocusOpenApps()
    {
        FocusSearch.FirstNavigable(AppTiles)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Y: switch to the window after the foreground one in strip order,
    ///     wrapping: an Alt+Tab step from the sheet. Nothing to do with fewer than two
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

    /// <summary>Routes the Open apps chips' and tray pills' presses to this window's events.</summary>
    /// <remarks>Called once from the constructor; the same routing serves the Game Mode taskbar.</remarks>
    private void AttachOpenAppsStrip()
    {
        OpenAppsStrip.Attach(TileScroller, TrayScroller, entry => WindowPicked?.Invoke(entry),
            (entry, contextMenu, pill) => TrayIconActivated?.Invoke(entry, contextMenu, AnchorBelow(pill)));
    }

    /// <summary>The screen point just below a pill's centre, where a tray app anchors its menu under the header.</summary>
    private static PixelPoint AnchorBelow(Control control)
    {
        return OpenAppsStrip.AnchorBelow(control);
    }
}
