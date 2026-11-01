using System.Collections.Generic;
using System.Linq;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>What the overlay's Themes view says about the themes and each listing.</summary>
/// <remarks>Pure, so the wording is tested without building a window.</remarks>
internal static class ThemesRows
{
    /// <summary>The home level's summary.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>How many themes are on, how many updates wait, and whether the feature is off.</returns>
    internal static string Summary(SteamThemesState state)
    {
        if (!state.Settings.Enabled)
        {
            return "Themes are off in Settings, so nothing is installed into Steam.";
        }

        if (state.Themes.Count == 0)
        {
            return "Restyle Big Picture with CSS Loader themes from DeckThemes.";
        }

        var line = $"{state.Themes.Count(theme => theme.Enabled)} of {state.Themes.Count} themes on";
        if (state.Updates > 0)
        {
            line += $", {state.Updates} update{(state.Updates == 1 ? "" : "s")} available";
        }

        return line + ".";
    }

    /// <summary>One installed theme's line.</summary>
    /// <param name="theme">The theme.</param>
    /// <returns>Whether it is on, its version and author, and whether the store has a newer one.</returns>
    internal static string Describe(SteamThemesInstalled theme)
    {
        List<string> parts = [theme.Enabled ? "On" : "Off"];
        if (theme.Status == ThemeStates.Outdated && theme.LatestVersion is { } latest)
        {
            parts.Add($"Update available ({latest})");
        }
        else
        {
            parts.Add(theme.Version);
        }

        if (theme.Author.Length > 0)
        {
            parts.Add(theme.Author);
        }

        if (theme.Hidden)
        {
            parts.Add("Hidden from Quick Access");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One store listing's line.</summary>
    /// <param name="item">The listing.</param>
    /// <returns>Its version, author, target, downloads and whether it is installed.</returns>
    internal static string DescribeListing(SteamThemesStoreItem item)
    {
        List<string> parts = [item.Version];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (item.Target.Length > 0)
        {
            parts.Add(item.Target);
        }

        parts.Add($"{item.Downloads} downloads");
        switch (item.LocalStatus)
        {
            case ThemeStates.Installed:
                parts.Add("Installed");
                break;
            case ThemeStates.Outdated:
                parts.Add("Installed, update available");
                break;
        }

        return string.Join(" · ", parts);
    }
}
