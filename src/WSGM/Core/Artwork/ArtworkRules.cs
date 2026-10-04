using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

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
