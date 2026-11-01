using System.Collections.Generic;

namespace WSGM.Shell;

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
