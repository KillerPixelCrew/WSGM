using System;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>WSGM-owned fixed package layout for profile manifests and hash-addressed artwork.</summary>
/// <remarks>
/// Callers must constrain the returned relative path below the already selected immutable package
/// directory. Display names, labels, source revisions, and notice paths never enter artwork
/// mapping.
/// </remarks>
public static class GlyphPackageLayout
{
    /// <summary>Returns the fixed profile-manifest path for one stable profile identifier.</summary>
    /// <param name="profileId">Validated package-scoped profile identifier.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string ProfileManifest(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > GlyphProfileLimits.MaxIdentifierLength
            || profileId.AsSpan().IndexOfAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-") >= 0)
        {
            throw new ArgumentException("Profile identifiers contain only ASCII letters, digits, '.', '_', and '-'.",
                nameof(profileId));
        }

        return $"glyphs/profiles/{profileId}.json";
    }

    /// <summary>Returns the fixed source-asset path for a locked asset.</summary>
    /// <param name="sha256">Canonical lowercase SHA-256.</param>
    /// <param name="format">Validated media type controlling the fixed extension.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string Asset(string sha256, GlyphAssetFormat format)
    {
        ValidateHash(sha256);
        string extension = format switch
        {
            GlyphAssetFormat.Svg => "svg",
            GlyphAssetFormat.Png => "png",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return $"glyphs/assets/{sha256}.{extension}";
    }

    private static void ValidateHash(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw new ArgumentException(
                "Content hash must be exactly 64 lowercase hexadecimal characters.",
                nameof(sha256));
        }
    }
}
