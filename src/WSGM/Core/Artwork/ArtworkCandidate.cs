namespace WSGM.Core;

/// <summary>One artwork candidate; the merge de-duplicates by <see cref="Url" />.</summary>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL for the picker grid.</param>
/// <param name="Width">Pixel width, or zero when the provider does not report one.</param>
/// <param name="Height">Pixel height, or zero when the provider does not report one.</param>
/// <param name="Extension">Provider-reported format hint; downloaded bytes are validated separately before application.</param>
/// <param name="ProviderId">Stable source identifier populated by the merger.</param>
/// <param name="ProviderName">Visible source attribution populated by the merger.</param>
/// <param name="Author">Artwork author, when reported.</param>
/// <param name="Style">Provider style identifier, when reported.</param>
/// <param name="Notes">Provider notes, when reported.</param>
/// <param name="Animated">Whether the result is animated.</param>
/// <param name="Nsfw">Whether the provider marks the result as adult content.</param>
/// <param name="Humor">Whether the provider marks the result as humor.</param>
/// <param name="Epilepsy">Whether the provider marks the result as flashing content.</param>
internal sealed record ArtworkCandidate(
    string Url,
    string Thumb,
    int Width,
    int Height,
    string Extension,
    string ProviderId = "",
    string ProviderName = "",
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);
