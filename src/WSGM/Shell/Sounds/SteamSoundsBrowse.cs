using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The store page last asked for.</summary>
/// <param name="Items">The listings on that page.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The page, from one, or zero before the store was asked.</param>
/// <param name="PageSize">How many listings a page holds.</param>
internal sealed record SteamSoundsBrowse(IReadOnlyList<SteamSoundsListing> Items, int Total, int Page, int PageSize);
