namespace WSGM.Shell;

/// <summary>One boot movie as the Animations page and the overlay show it, listed or in the library.</summary>
/// <param name="Id">The repository's id, or the library's id for a brought file.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The uploader, or empty.</param>
/// <param name="ThumbnailUrl">A still, or null.</param>
/// <param name="PreviewUrl">The movie, playable in the page, or null.</param>
/// <param name="Description">The post's text.</param>
/// <param name="Likes">How often it was liked.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Updated">When it was last changed, as a date, or empty.</param>
/// <param name="Downloaded">Whether the library holds it.</param>
/// <param name="Custom">Whether it is a file the user brought.</param>
internal sealed record SteamAnimationsItem(
    string Id,
    string Name,
    string Author,
    string? ThumbnailUrl,
    string? PreviewUrl,
    string Description,
    int Likes,
    int Downloads,
    string Updated,
    bool Downloaded,
    bool Custom);
