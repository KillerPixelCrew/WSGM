using System;

namespace WSGM.Core;

/// <summary>The one spelling of each artwork slot that pages, commands and stored choices use.</summary>
/// <remarks>
///     The artwork page's tabs and the Game Library's commands both name slots by these ids, so the
///     two cannot drift onto different words for the same slot.
/// </remarks>
public static class ArtworkAssetNames
{
    /// <summary>A slot's id.</summary>
    /// <param name="asset">The slot.</param>
    /// <returns><c>grid</c>, <c>hero</c>, <c>logo</c>, <c>wide</c> or <c>icon</c>.</returns>
    public static string ToId(ArtworkAsset asset)
    {
        return asset switch
        {
            ArtworkAsset.Grid => "grid",
            ArtworkAsset.Hero => "hero",
            ArtworkAsset.Logo => "logo",
            ArtworkAsset.Wide => "wide",
            ArtworkAsset.Icon => "icon",
            _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, "Unknown artwork slot.")
        };
    }

    /// <summary>Reads a slot from its id.</summary>
    /// <param name="id">The id, compared exactly.</param>
    /// <param name="asset">The slot, when this returns true.</param>
    /// <returns>Whether the id names a slot.</returns>
    public static bool TryParse(string? id, out ArtworkAsset asset)
    {
        (asset, var known) = id switch
        {
            "grid" => (ArtworkAsset.Grid, true),
            "hero" => (ArtworkAsset.Hero, true),
            "logo" => (ArtworkAsset.Logo, true),
            "wide" => (ArtworkAsset.Wide, true),
            "icon" => (ArtworkAsset.Icon, true),
            _ => (default, false)
        };
        return known;
    }
}
