using System.Collections.Generic;

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

/// <summary>One way the Browse tab orders the repository's list.</summary>
/// <param name="Id">What the page sends back.</param>
/// <param name="Label">What it shows, as Animation Changer names it.</param>
internal sealed record SteamAnimationsSort(string Id, string Label);

/// <summary>The Browse tab: the repository's list, sorted and searched on the page's behalf.</summary>
/// <param name="Sort">The id of one of <paramref name="Sorts" />.</param>
/// <param name="Sorts">The orders offered, which the page and the overlay draw from.</param>
/// <param name="Search">The search text.</param>
/// <param name="Items">
///     The first of what matches, in the sort's order: a page more with each <c>more</c>. The
///     repository lists thousands, and a card for each stalls Steam's renderer.
/// </param>
/// <param name="Matched">How many match the search in all.</param>
/// <param name="Total">How many the repository lists in all.</param>
/// <param name="Loading">Whether the repository is being asked.</param>
/// <param name="Error">Why the last request failed, or null.</param>
internal sealed record SteamAnimationsBrowse(
    string Sort,
    IReadOnlyList<SteamAnimationsSort> Sorts,
    string Search,
    IReadOnlyList<SteamAnimationsItem> Items,
    int Matched,
    int Total,
    bool Loading,
    string? Error);

/// <summary>What the pages know beyond the choice and the lists.</summary>
/// <param name="ShuffleOnStart">Whether the boot movie is picked anew when WSGM starts.</param>
/// <param name="BootVolume">The boot movie's volume in percent of its file's.</param>
/// <param name="LibraryPath">Where the movies are kept.</param>
/// <param name="OverridesPath">Where the override is written, or null without Steam.</param>
/// <param name="RestartNeeded">Whether the override changed since Steam started, so a restart shows it.</param>
internal sealed record SteamAnimationsSettings(
    bool ShuffleOnStart,
    int BootVolume,
    string LibraryPath,
    string? OverridesPath,
    bool RestartNeeded);

/// <summary>Everything the Animations page, the Quick Access section and the overlay draw.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>library</c> or <c>settings</c>.</param>
/// <param name="Selected">The library id Big Picture starts with, empty for Steam's own movie.</param>
/// <param name="StockName">What the empty choice is called.</param>
/// <param name="Library">The library, downloads first.</param>
/// <param name="Browse">The repository's list.</param>
/// <param name="Detail">The movie opened for a closer look, or null.</param>
/// <param name="Settings">What else the pages know.</param>
/// <param name="Busy">Whether a download or a copy is running.</param>
/// <param name="Notice">A line worth reading, or null.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamAnimationsState(
    string ActiveTab,
    string Selected,
    string StockName,
    IReadOnlyList<SteamAnimationsItem> Library,
    SteamAnimationsBrowse Browse,
    SteamAnimationsItem? Detail,
    SteamAnimationsSettings Settings,
    bool Busy,
    string? Notice,
    string? Error,
    long Revision);
