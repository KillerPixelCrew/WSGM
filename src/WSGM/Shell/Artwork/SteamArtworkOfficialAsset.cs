namespace WSGM.Shell;

/// <summary>One official Steam asset offered alongside community artwork.</summary>
/// <param name="Id">Opaque current official-result identity accepted by ApplyOfficialAsync.</param>
/// <param name="Label">Displayed description of the official image.</param>
/// <param name="ImageUrl">Official Steam image URL.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Format">Image file extension or format label.</param>
internal sealed record SteamArtworkOfficialAsset(
    string Id,
    string Label,
    string ImageUrl,
    int Width,
    int Height,
    string Format);
