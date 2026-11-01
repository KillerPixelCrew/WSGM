namespace WSGM.Core;

/// <summary>One boot movie SteamDeckRepo lists, as Animation Changer reads a post.</summary>
/// <param name="Id">The post's id, which names the download.</param>
/// <param name="Name">The post's title.</param>
/// <param name="Author">The uploader's Steam name.</param>
/// <param name="Description">The post's text.</param>
/// <param name="ThumbnailUrl">A still, or empty.</param>
/// <param name="PreviewUrl">The movie itself, playable in a page, or empty.</param>
/// <param name="DownloadUrl">Where the movie is downloaded from.</param>
/// <param name="Likes">How often it was liked.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Updated">When it was last changed, as the repository formats it.</param>
public sealed record AnimationListing(
    string Id,
    string Name,
    string Author,
    string Description,
    string ThumbnailUrl,
    string PreviewUrl,
    string DownloadUrl,
    int Likes,
    int Downloads,
    string Updated);
