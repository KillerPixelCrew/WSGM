using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The store's listings for the last search, the pages loaded so far appended in order.</summary>
/// <param name="Items">The listings loaded so far.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The last page loaded, from one, or zero before the store was asked.</param>
/// <param name="PageSize">How many listings a page holds.</param>
/// <param name="Search">The search the listings answer.</param>
internal sealed record SteamSoundsBrowse(
    IReadOnlyList<SteamSoundsListing> Items,
    int Total,
    int Page,
    int PageSize,
    string Search);
