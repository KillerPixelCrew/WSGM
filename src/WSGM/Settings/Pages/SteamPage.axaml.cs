using System.Linq;
using Avalonia.Controls;

namespace WSGM.Settings.Pages;

/// <summary>
///     The Steam section: Big Picture status, the session, the integration switch with its nested pages, and
///     defaults for new titles. Inherits the window's <see cref="SettingsViewModel" /> DataContext.
/// </summary>
public partial class SteamPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SteamPage()
    {
        InitializeComponent();
    }

    /// <summary>Says on each nested page's row what it holds now, so the row reads without opening it.</summary>
    /// <param name="model">The window's view model.</param>
    internal void RefreshSummaries(SettingsViewModel model)
    {
        LibrarySummary.Text = "Filter tabs, SD-card libraries, card formatting, Home carousel · "
                              + Count(model.CefLibraryTabs, model.CefCardManager, model.CefSdFormat,
                                  model.SteamStorageFormat, model.CefConnectedLibraryCarousel,
                                  model.CefCarouselShowUninstalled);
        ClientSummary.Text = "Wi-Fi indicator, Quick Access bridge, keep awake, download sorting · "
                             + Count(model.CefWifiIndicator, model.CefNativeQuickAccess, model.CefDownloadKeepAwake,
                                 model.CefDownloadQueueSort);
        SteamInputSummary.Text = model.SteamInputManagementEnabled ? "Managed by WSGM" : "Left to Steam";
        ArtworkSummary.Text =
            (string.IsNullOrWhiteSpace(model.ArtworkSteamGridDbApiKey) ? "No SteamGridDB key" : "SteamGridDB key set")
            + " · Screenscraper " + (model.ArtworkScreenscraperEnabled ? "on" : "off");
    }

    private static string Count(params bool[] switches)
    {
        var on = switches.Count(static value => value);
        return on == switches.Length ? "all on" : on == 0 ? "all off" : $"{on} of {switches.Length} on";
    }
}
