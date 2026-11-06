using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Steam › Artwork: the artwork providers and tab order.</summary>
public partial class SteamArtworkPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SteamArtworkPage()
    {
        InitializeComponent();
    }

    /// <summary>Opens the controller keyboard for one of the otherwise skipped text fields.</summary>
    /// <remarks>Gamepad navigation skips text boxes, so each one needs its own explicit way in.</remarks>
    private void OnEditTextWithController(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        switch ((sender as Button)?.Tag as string)
        {
            case "SteamGridDbKey":
                window.ShowOnScreenKeyboard(SteamGridDbKeyBox, "SteamGridDB key");
                break;
            case "ScreenscraperUser":
                window.ShowOnScreenKeyboard(ScreenscraperUserBox, "Screenscraper account");
                break;
            case "ScreenscraperPassword":
                window.ShowOnScreenKeyboard(ScreenscraperPasswordBox, "Screenscraper password");
                break;
        }
    }
}
