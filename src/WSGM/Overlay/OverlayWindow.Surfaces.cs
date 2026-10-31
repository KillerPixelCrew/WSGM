using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The transient surfaces over the deck: utilities, the keyboard, the picker and the power menu.</summary>
    internal OverlaySurfaceStack Surfaces { get; }

    /// <summary>Opens the keyboard of the overlay window that hosts a control.</summary>
    /// <param name="owner">A control in the overlay window.</param>
    /// <param name="prompt">Accessible field label.</param>
    /// <param name="initial">Text copied into the editor.</param>
    /// <param name="maxLength">Character limit, or zero for no explicit limit.</param>
    /// <param name="onAccept">UI-thread callback invoked once with accepted text; cancellation does not invoke it.</param>
    /// <param name="multiline">Whether Enter starts a new line instead of accepting.</param>
    /// <param name="changed">UI-thread callback with the text after every edit, or null.</param>
    /// <returns>True when a keyboard was created; false outside an overlay window or after it closed.</returns>
    internal static bool RequestTextFor(Control owner, string prompt, string initial, int maxLength,
        Action<string> onAccept, bool multiline = false, Action<string>? changed = null)
    {
        return GetTopLevel(owner) is OverlayWindow window
               && window.Surfaces.RequestText(prompt, initial, maxLength, onAccept, multiline, changed);
    }

    /// <summary>Requests the centred power menu through the surface owner.</summary>
    internal event Action? PowerMenuRequested;

    /// <summary>Projects the Power destination's real action controls into the centred menu.</summary>
    internal void ShowPowerMenu()
    {
        Surfaces.CloseAll();
        _powerConfirms.Reset();
        Surfaces.ShowPowerMenu(PanelPowerActions.MenuActions(PanelPowerSession.DesktopButton),
            PanelPowerActions.ConfirmedActions);
    }

    private void CoverMedia(bool covered)
    {
        BrowserHost.Cover(covered);
        foreach (var viewport in this.GetVisualDescendants().OfType<OverlayMediaPreview.MediaViewport>())
        {
            viewport.Suspend(covered);
        }
    }
}
