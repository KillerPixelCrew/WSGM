using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace WSGM.Settings.Pages;

/// <summary>The Startup settings page: the ordered startup-app editor (the only
/// internally scrolling Settings surface), launch delays and the boot-splash
/// toggle. Inherits the window's <see cref="SettingsViewModel"/> DataContext;
/// the async file pickers stay in this code-behind (StorageProvider needs the
/// visual tree's TopLevel).</summary>
public partial class StartupPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public StartupPage() => InitializeComponent();

    private async void OnAddApp(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        // A detected suggestion adds itself; "Choose a program…" opens the picker.
        if (viewModel.AddSelectedStartupApp())
        {
            return;
        }
        var path = await PickExeAsync();
        if (path is not null)
        {
            viewModel.StartupApps.Add(new StartupAppRow { Path = path, Enabled = true });
        }
    }

    private async void OnBrowseStartupApp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            var path = await PickExeAsync();
            if (path is not null)
            {
                row.Path = path;
            }
        }
    }

    private async System.Threading.Tasks.Task<string?> PickExeAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return null;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select application",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Applications") { Patterns = ["*.exe"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
