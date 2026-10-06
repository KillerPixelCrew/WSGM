using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     An artwork slot. The numeric values are Steam's own <c>eAssetType</c>
///     (capsule/portrait = 0, hero = 1, logo = 2, wide capsule = 3, icon = 4) so they pass
///     straight into <see cref="SteamArtwork" />'s <c>SetCustomArtworkForApp</c> call.
/// </summary>
public enum ArtworkAsset
{
    /// <summary>Portrait capsule (600×900).</summary>
    Grid = 0,

    /// <summary>Hero banner (1920×620).</summary>
    Hero = 1,

    /// <summary>Transparent logo.</summary>
    Logo = 2,

    /// <summary>Wide capsule (460×215).</summary>
    Wide = 3,

    /// <summary>Icon.</summary>
    Icon = 4
}

/// <summary>The one spelling of each artwork slot that pages, commands and stored choices use.</summary>
/// <remarks>
///     The artwork page's tabs and the Game Library's commands both name slots by these ids, so the
///     two cannot drift onto different words for the same slot.
/// </remarks>
internal static class ArtworkAssetNames
{
    internal static IReadOnlyList<(ArtworkAsset Asset, string Id, string Label)> Ordered { get; } =
    [
        (ArtworkAsset.Grid, "grid", "Capsule"),
        (ArtworkAsset.Wide, "wide", "Wide Capsule"),
        (ArtworkAsset.Hero, "hero", "Hero"),
        (ArtworkAsset.Logo, "logo", "Logo"),
        (ArtworkAsset.Icon, "icon", "Icon")
    ];

    /// <summary>A slot's id.</summary>
    /// <param name="asset">The slot.</param>
    /// <returns><c>grid</c>, <c>hero</c>, <c>logo</c>, <c>wide</c> or <c>icon</c>.</returns>
    public static string ToId(ArtworkAsset asset)
    {
        foreach (var slot in Ordered)
        {
            if (slot.Asset == asset)
            {
                return slot.Id;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(asset), asset, "Unknown artwork slot.");
    }

    /// <summary>Reads a slot from its id.</summary>
    /// <param name="id">The id, compared exactly.</param>
    /// <param name="asset">The slot, when this returns true.</param>
    /// <returns>Whether the id names a slot.</returns>
    public static bool TryParse(string? id, out ArtworkAsset asset)
    {
        foreach (var slot in Ordered)
        {
            if (slot.Id == id)
            {
                asset = slot.Asset;
                return true;
            }
        }

        asset = default;
        return false;
    }
}
