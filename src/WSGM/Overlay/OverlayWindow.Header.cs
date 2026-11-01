using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly HeaderProfileScope _profileScope;

    private void OnManageProfiles(object? sender, RoutedEventArgs e)
    {
        if (_performanceSource is not { } source)
        {
            return;
        }

        var editor = new ApplicationProfilesView(source, _deviceLifetime.Token);
        Surfaces.ShowUtility(editor, "Application profiles", editor.DefaultFocusTarget);
    }

    private void OnRadioTileClicked(object? sender, RoutedEventArgs e)
    {
        // The panel stays in this window's focus scope and navigation owner.
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

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
    }
}
