using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Normalizes the stored artwork configuration in place before consumers use it.</summary>
internal static class ArtworkRules
{
    /// <summary>Brings the artwork section into a shape the Steam browser can render.</summary>
    /// <param name="artwork">The section to normalize in place.</param>
    /// <remarks>
    ///     Internal so its rules can be tested directly. The tab order is a permutation of the known
    ///     tabs: unknown and duplicate ids are dropped and missing ones appended, so a hand-edited
    ///     value cannot hide a tab the show switches still say is visible, and the default tab always
    ///     names one that exists.
    /// </remarks>
    /// <returns>An empty diagnostic list; these repairs do not produce warning entries.</returns>
    internal static IReadOnlyList<string> Normalize(ArtworkConfig artwork)
    {
        artwork.SteamGridDbApiKey = artwork.SteamGridDbApiKey?.Trim() ?? "";
        artwork.ScreenscraperUser = artwork.ScreenscraperUser?.Trim() ?? "";
        artwork.ScreenscraperUserPassword = artwork.ScreenscraperUserPassword ?? "";

        var known = ArtworkConfig.DefaultTabOrder.Split(',');
        var ordered = (artwork.TabOrder ?? "").Split(',')
            .Select(static tab => tab.Trim())
            .Where(tab => known.Contains(tab, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(known.Where(tab => !ordered.Contains(tab, StringComparer.Ordinal)));
        artwork.TabOrder = string.Join(',', ordered);

        var requested = artwork.DefaultTab?.Trim() ?? "";
        artwork.DefaultTab = known.Contains(requested, StringComparer.Ordinal) ? requested : ordered[0];
        return [];
    }
}
