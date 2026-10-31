using Avalonia.Controls;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The sheet's display, content scale, slide-in and glass backdrop.</summary>
    internal OverlayPlacement Placement { get; }

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Classes.Set("compact", e.NewSize.Width < 1150);
        TrayScroller.MaxWidth = OverlayPlacement.ComputeTrayMaxWidth(e.NewSize.Width, 1);
    }
}
