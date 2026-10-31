using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>One boot movie in WSGM's library: a download with its listing, or a file the user brought.</summary>
/// <param name="Id">The listing's id, or <c>custom:</c> and the file's name.</param>
/// <param name="Name">The name the pages show.</param>
/// <param name="Path">The movie's full path.</param>
/// <param name="Listing">The repository's listing, or null for a brought file.</param>
public sealed record AnimationEntry(
    string Id,
    string Name,
    [property: JsonIgnore] string Path,
    AnimationListing? Listing)
{
    /// <summary>The prefix a brought file's id carries.</summary>
    public const string CustomPrefix = "custom:";

    /// <summary>Whether the entry is a file the user brought rather than a download.</summary>
    public bool IsCustom => Listing is null;
}
