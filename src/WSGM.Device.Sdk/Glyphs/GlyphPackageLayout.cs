using System;
using System.Buffers;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>WSGM-owned fixed package layout for profile manifests and identified artwork.</summary>
/// <remarks>
///     Callers must constrain the returned relative path below the already selected immutable package
///     directory. Display names, labels, source revisions, and notice paths never enter artwork
///     mapping.
/// </remarks>
public static class GlyphPackageLayout
{
    private static readonly SearchValues<char> IdentifierCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

    /// <summary>Returns the fixed profile-manifest path for one stable profile identifier.</summary>
    /// <param name="profileId">Validated package-scoped profile identifier.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string ProfileManifest(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > GlyphProfileLimits.MaxIdentifierLength
            || profileId.AsSpan().IndexOfAnyExcept(IdentifierCharacters) >= 0)
        {
            throw new ArgumentException("Profile identifiers contain only ASCII letters, digits, '.', '_', and '-'.",
                nameof(profileId));
        }

        return $"glyphs/profiles/{profileId}.json";
    }

    /// <summary>Returns the fixed source-asset path for one declared asset.</summary>
    /// <param name="assetId">Validated package-scoped asset identifier.</param>
    /// <param name="format">Validated media type controlling the fixed extension.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string Asset(string assetId, GlyphAssetFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        if (assetId.Length > GlyphProfileLimits.MaxIdentifierLength
            || assetId.AsSpan().IndexOfAnyExcept(IdentifierCharacters) >= 0)
        {
            throw new ArgumentException("Asset identifiers contain only ASCII letters, digits, '.', '_', and '-'.",
                nameof(assetId));
        }

        var extension = format switch
        {
            GlyphAssetFormat.Svg => "svg",
            GlyphAssetFormat.Png => "png",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return $"glyphs/assets/{assetId}.{extension}";
    }
}
