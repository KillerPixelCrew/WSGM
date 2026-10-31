namespace WSGM.Shell;

/// <summary>One image a title could use for one artwork type.</summary>
/// <param name="Url">The full-size image.</param>
/// <param name="Thumb">A smaller copy for the page, or the same URL.</param>
/// <param name="Provider">Who supplied it, as shown on screen.</param>
/// <param name="Catalog">Whether it came from the title's own source rather than an artwork provider.</param>
/// <param name="Width">Its width, or zero when unknown.</param>
/// <param name="Height">Its height, or zero when unknown.</param>
internal sealed record GameLibraryArtworkOption(
    string Url,
    string Thumb,
    string Provider,
    bool Catalog,
    int Width,
    int Height);
