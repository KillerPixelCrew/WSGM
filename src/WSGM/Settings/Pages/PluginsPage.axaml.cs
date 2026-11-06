using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Installed common plugins, their activation choices and package actions.</summary>
public partial class PluginsPage : UserControl
{
    /// <summary>Loads the compiled common-plugin page.</summary>
    public PluginsPage()
    {
        InitializeComponent();
    }

    /// <summary>Shows or hides the plugins that do not apply to this device.</summary>
    private void OnShowUnavailable(object? sender, RoutedEventArgs e)
    {
        UnavailableList.IsVisible = !UnavailableList.IsVisible;
        ShowUnavailable.Content = UnavailableList.IsVisible ? "Hide" : "Show";
    }
}
