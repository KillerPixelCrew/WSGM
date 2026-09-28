using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>One animation SteamDeckRepo lists, as Animation Changer reads a post.</summary>
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
/// <param name="Target"><see cref="AnimationTargets.Boot" /> or <see cref="AnimationTargets.Suspend" />.</param>
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
    string Updated,
    string Target);

/// <summary>One animation in WSGM's library: a download with its listing, or a file the user brought.</summary>
/// <param name="Id">The listing's id, or <c>custom:</c> and the file's name.</param>
/// <param name="Name">The name the pages show.</param>
/// <param name="Author">The author, or empty.</param>
/// <param name="Target">What it is for; a brought file fits every slot.</param>
/// <param name="Path">The movie's full path.</param>
/// <param name="Listing">The repository's listing, or null for a brought file.</param>
public sealed record AnimationEntry(
    string Id,
    string Name,
    string Author,
    string Target,
    [property: JsonIgnore] string Path,
    AnimationListing? Listing)
{
    /// <summary>The prefix a brought file's id carries.</summary>
    public const string CustomPrefix = "custom:";

    /// <summary>Whether the entry is a file the user brought rather than a download.</summary>
    public bool IsCustom => Listing is null;
}
