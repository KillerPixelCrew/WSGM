using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>
///     Startup › Add app: companion apps found on this PC, then any program. Adding one puts it at the end of the
///     startup order and opens its editor, so the arguments and options follow straight on.
/// </summary>
public partial class StartupAddPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public StartupAddPage()
    {
        InitializeComponent();
    }

    private void OnAddSuggestion(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupSuggestion suggestion)
        {
            Add(suggestion.Path, suggestion.Elevated, suggestion.Label);
        }
    }

    private void OnBrowse(object? sender, RoutedEventArgs e)
    {
        PageActions.Observe(this, BrowseAsync, "Choosing a program");
    }

    private async Task BrowseAsync()
    {
        if (await StartupPage.PickProgramAsync(this) is { } path)
        {
            Add(path, false, "");
        }
    }

    private void Add(string path, bool elevated, string label)
    {
        if (DataContext is SettingsViewModel model && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            window.PageStartup.Edit(model.AddStartupApp(path, elevated, label));
        }
    }
}
