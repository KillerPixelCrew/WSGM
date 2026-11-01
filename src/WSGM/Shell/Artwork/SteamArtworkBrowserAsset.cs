namespace WSGM.Shell;

/// <summary>One artwork result rendered by Steam's native artwork browser page.</summary>
/// <param name="Id">Opaque current-result identity accepted by ApplyAsync; it is not a provider URL.</param>
/// <param name="ImageUrl">Provider image URL used for preview and host download.</param>
/// <param name="ThumbnailUrl">Preview URL, falling back to the full image URL when the provider has no thumbnail.</param>
/// <param name="Width">Provider-reported image width in pixels.</param>
/// <param name="Height">Provider-reported image height in pixels.</param>
/// <param name="Format">Provider file extension or format label.</param>
/// <param name="Provider">Provider display name.</param>
/// <param name="Author">Creator attribution, or null when unavailable.</param>
/// <param name="Style">Provider style tag, or null.</param>
/// <param name="Notes">Provider notes, or null.</param>
/// <param name="Animated">Whether the provider marks this image animated.</param>
/// <param name="Nsfw">Whether the provider marks this image as adult content.</param>
/// <param name="Humor">Whether the provider marks this image as humor.</param>
/// <param name="Epilepsy">Whether the provider marks this image as containing flashing imagery.</param>
internal sealed record SteamArtworkBrowserAsset(
    string Id,
    string ImageUrl,
    string ThumbnailUrl,
    int Width,
    int Height,
    string Format,
    string Provider,
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);
