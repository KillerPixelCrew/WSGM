using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WSGM.Controls;

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

    private void OnOpenPowerMenu(object? sender, RoutedEventArgs e)
    {
        PowerMenuRequested?.Invoke();
    }

    /// <summary>Projects the Power destination's real action controls into the centred menu.</summary>
    internal void ShowPowerMenu()
    {
        Surfaces.CloseAll();
        _powerConfirms.Reset();
        var sources = PanelPowerActions.Children.OfType<ActionButton>().Where(source => source.IsVisible).ToList();
        // Placement differs from the linear page; new Power actions are included automatically.
        if (sources.Remove(SignOutButton))
        {
            var shutdown = sources.IndexOf(ShutdownButton);
            sources.Insert(shutdown >= 0 ? shutdown : sources.Count, SignOutButton);
        }

        if (DesktopButton.IsVisible)
        {
            sources.Insert(Math.Min(2, sources.Count), DesktopButton);
        }

        Surfaces.ShowPowerMenu(sources, [RestartButton, SignOutButton]);
    }

    private void CoverMedia(bool covered)
    {
        CoverBrowserPage(covered);
        foreach (var viewport in this.GetVisualDescendants().OfType<OverlayMediaPreview.MediaViewport>())
        {
            viewport.Suspend(covered);
        }
    }
}
