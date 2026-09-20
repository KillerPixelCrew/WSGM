using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Per-monitor Desktop and Game display-profile settings.</summary>
public partial class DisplayPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public DisplayPage()
    {
        InitializeComponent();
    }

    private void OnRefreshAudio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { CurrentAudioProfile: { } audio })
        {
            audio.RefreshEndpoints();
        }
    }
}
