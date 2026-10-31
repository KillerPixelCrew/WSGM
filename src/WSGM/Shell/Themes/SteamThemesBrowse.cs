using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The Browse tab: what was asked for and what the store answered.</summary>
/// <param name="Filter">The target shown, or <c>All</c>.</param>
/// <param name="Order">The order, one of the store's names.</param>
/// <param name="Search">The search text.</param>
/// <param name="Filters">Every target and its count, once the store has been asked.</param>
/// <param name="Orders">The store's order names.</param>
/// <param name="Items">The listings loaded so far.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The last page loaded.</param>
/// <param name="Loading">Whether a request is in flight.</param>
/// <param name="Error">Why the last request failed, or null.</param>
internal sealed record SteamThemesBrowse(
    string Filter,
    string Order,
    string Search,
    IReadOnlyDictionary<string, int> Filters,
    IReadOnlyList<string> Orders,
    IReadOnlyList<SteamThemesStoreItem> Items,
    int Total,
    int Page,
    bool Loading,
    string? Error);
