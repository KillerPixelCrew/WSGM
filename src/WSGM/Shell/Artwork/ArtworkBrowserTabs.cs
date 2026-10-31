using System;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The artwork browser's tab layout and each slot tab's default result filter, as every surface's
///     session shows them.
/// </summary>
internal static class ArtworkBrowserTabs
{
    /// <summary>The tab that manages a title's current artwork instead of browsing one slot.</summary>
    internal const string ManageTab = "manage";

    private static readonly SteamArtworkBrowserTab[] AllTabs =
    [
        .. ArtworkAssetNames.Ordered.Select(slot => new SteamArtworkBrowserTab(slot.Id, slot.Label)),
        new(ManageTab, "Manage", true)
    ];

    /// <summary>The tabs the configuration shows, in its order.</summary>
    /// <param name="configuration">The current artwork configuration.</param>
    /// <returns>The visible known tabs without duplicates, or every tab when the configuration leaves none.</returns>
    internal static SteamArtworkBrowserTab[] Configured(ArtworkConfig configuration)
    {
        var lookup = AllTabs.ToDictionary(tab => tab.Id, StringComparer.Ordinal);
        SteamArtworkBrowserTab[] tabs =
        [
            .. configuration.TabOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => configuration.IsTabVisible(id) && lookup.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .Select(id => lookup[id])
        ];
        return tabs.Length == 0 ? AllTabs : tabs;
    }

    /// <summary>The filter a slot tab starts from before the user saves one.</summary>
    /// <param name="tab">The tab id.</param>
    /// <returns>The slot's usual styles, dimensions and image types.</returns>
    internal static SteamArtworkBrowserFilter DefaultFilter(string tab)
    {
        var styles = tab switch
        {
            "hero" => new[] { "alternate", "blurred", "material" },
            "logo" => ["official", "white", "black", "custom"],
            "icon" => ["official", "custom"],
            _ => ["alternate", "white_logo", "no_logo", "blurred", "material"]
        };
        var dimensions = tab switch
        {
            "grid" => new[] { "600x900", "342x482", "660x930" },
            "wide" => ["460x215", "920x430"],
            "hero" => ["1920x620", "3840x1240", "1600x650"],
            "icon" => new[]
            {
                "1024", "768", "512", "310", "256", "194", "192", "180", "160", "152", "150",
                "144", "128", "120", "114", "100", "96", "90", "80", "76", "72", "64", "60", "57",
                "56", "54", "48", "40", "35", "32", "28", "24", "20", "16", "14", "10", "8"
            },
            _ => []
        };
        var mimes = tab switch
        {
            "logo" => new[] { "image/png", "image/webp" },
            "icon" => ["image/png", "image/vnd.microsoft.icon"],
            _ => ["image/png", "image/jpeg", "image/webp"]
        };
        return new SteamArtworkBrowserFilter(styles, dimensions, mimes);
    }

    /// <summary>A filter the user saved, as the page shows it.</summary>
    /// <param name="filter">The saved filter.</param>
    /// <returns>The same choices as a browser filter.</returns>
    internal static SteamArtworkBrowserFilter FromSaved(ArtworkSavedFilter filter)
    {
        return new SteamArtworkBrowserFilter(
            filter.Styles,
            filter.Dimensions,
            filter.Mimes,
            filter.Static,
            filter.Animated,
            filter.Adult,
            filter.Humor,
            filter.Epilepsy,
            filter.Untagged);
    }
}
