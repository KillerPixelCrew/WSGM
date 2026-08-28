using System;

namespace WSGM.Device.Contracts.Glyphs;

/// <summary>WSGM-owned fixed package layout derived only from validated content hashes.</summary>
/// <remarks>
/// Callers must constrain the returned relative path below the already selected immutable package
/// directory. Profile IDs, display names, labels, provenance text, and plugin paths never enter this
/// mapping.
/// </remarks>
public static class GlyphPackageLayout
{
    /// <summary>Returns the fixed profile-manifest path for canonical manifest bytes.</summary>
    /// <param name="sha256">Canonical lowercase SHA-256.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string ProfileManifest(string sha256)
    {
        ValidateHash(sha256);
        return $"glyphs/profiles/{sha256}.json";
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

    /// <summary>Returns the fixed generated output path for one locked source asset.</summary>
    /// <param name="sha256">Canonical lowercase source SHA-256.</param>
    /// <param name="format">Validated media type controlling the fixed extension.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string GeneratedAsset(string sha256, GlyphAssetFormat format)
    {
        ValidateHash(sha256);
        string extension = format switch
        {
            GlyphAssetFormat.Svg => "svg",
            GlyphAssetFormat.Png => "png",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return $"glyphs/generated/{sha256}.{extension}";
    }

    /// <summary>Returns the fixed notice path for a hash-pinned license or attribution text.</summary>
    /// <param name="sha256">Canonical lowercase SHA-256.</param>
    /// <returns>Forward-slash relative package path.</returns>
    public static string Notice(string sha256)
    {
        ValidateHash(sha256);
        return $"glyphs/notices/{sha256}.txt";
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
