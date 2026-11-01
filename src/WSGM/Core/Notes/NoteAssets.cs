using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>Image asset identities.</summary>
/// <remarks>
///     An asset is named by the SHA-256 of its bytes and its format, so the same screenshot inserted twice is
///     one file, and a name can never point at different content.
/// </remarks>
public static partial class NoteAssets
{
    /// <summary>Whether a value is a well-formed asset id.</summary>
    /// <param name="asset">The candidate.</param>
    /// <returns>True for 64 lowercase hexadecimal digits and a <c>.jpg</c> or <c>.png</c> extension.</returns>
    public static bool IsAssetId(string? asset)
    {
        return asset is not null && AssetPattern().IsMatch(asset);
    }

    [GeneratedRegex("^[0-9a-f]{64}\\.(jpg|png)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();
}
