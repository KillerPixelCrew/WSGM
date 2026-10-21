using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>Per-monitor Desktop and Game display-profile settings.</summary>
public partial class DisplayPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public DisplayPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.ScaledResolutions.RefreshCommand.Execute(null);
            }
        };
    }

    private void OnEditScaledDimension(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            var width = (sender as Button)?.Tag as string == "Width";
            window.ShowOnScreenKeyboard(width ? ScaledWidthBox : ScaledHeightBox,
                width ? "Source width in pixels" : "Source height in pixels");
        }
    }

    private void OnRefreshAudio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { CurrentAudioProfile: { } audio })
        {
            Log.Observe(audio.RefreshEndpointsAsync(), "Settings audio discovery");
        }
    }
}
