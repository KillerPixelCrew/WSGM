using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>
///     The About settings page: version, licence, the project links and the people and projects
///     WSGM thanks, all from <see cref="Credits" />. The only code is opening a link in the default
///     browser; Settings runs on the desktop, where that is the expected thing for a link to do.
/// </summary>
public partial class AboutPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public AboutPage()
    {
        InitializeComponent();
    }

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string url || !url.StartsWith("https://", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"About: could not open {url}: {ex.Message}");
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.StatusText = $"Could not open {url}";
            }
        }
    }
}
