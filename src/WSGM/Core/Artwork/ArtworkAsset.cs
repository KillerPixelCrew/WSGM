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
