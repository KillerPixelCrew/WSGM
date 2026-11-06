using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Startup › Edit app: the program, its arguments and how it runs.</summary>
public partial class StartupAppPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public StartupAppPage()
    {
        InitializeComponent();
    }

    private void OnType(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        switch ((sender as Button)?.Tag as string)
        {
            case "Label":
                window.ShowOnScreenKeyboard(NameBox, "App name");
                break;
            case "Args":
                window.ShowOnScreenKeyboard(ArgsBox, "Startup arguments");
                break;
            default:
                window.ShowOnScreenKeyboard(PathBox, "Startup program");
                break;
        }
    }

    private void OnBrowse(object? sender, RoutedEventArgs e)
    {
        PageActions.Observe(this, BrowseAsync, "Choosing a program");
    }

    private async Task BrowseAsync()
    {
        if (DataContext is SettingsViewModel { EditedStartupApp: { } row }
            && await StartupPage.PickProgramAsync(this) is { } path)
        {
            row.Path = path;
        }
    }
}
