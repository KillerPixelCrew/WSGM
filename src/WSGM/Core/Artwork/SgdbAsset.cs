namespace WSGM.Core;

/// <summary>One artwork candidate from SteamGridDB.</summary>
/// <param name="Id">SteamGridDB asset id.</param>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL (for the picker grid).</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Extension">Provider-reported format hint; downloaded bytes are validated separately before application.</param>
/// <param name="Author">Artwork author.</param>
/// <param name="Style">SteamGridDB style identifier.</param>
/// <param name="Notes">SteamGridDB notes.</param>
/// <param name="Animated">Whether the result is animated.</param>
/// <param name="Nsfw">Whether the result is adult-tagged.</param>
/// <param name="Humor">Whether the result is humor-tagged.</param>
/// <param name="Epilepsy">Whether the result is flashing-content-tagged.</param>
// ReSharper disable once NotAccessedPositionalProperty.Global
internal sealed record SgdbAsset(
    int Id,
    string Url,
    string Thumb,
    int Width,
    int Height,
    string Extension,
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);
