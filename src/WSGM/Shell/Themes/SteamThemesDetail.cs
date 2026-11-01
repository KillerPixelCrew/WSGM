using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One theme opened from the store.</summary>
/// <param name="Item">Its listing.</param>
/// <param name="Description">The author's description.</param>
/// <param name="ImageUrls">Its screenshots.</param>
/// <param name="Dependencies">The themes it needs.</param>
/// <param name="Loading">Whether the details are still being read.</param>
/// <param name="Error">Why they could not be read, or null.</param>
internal sealed record SteamThemesDetail(
    SteamThemesStoreItem Item,
    string Description,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<SteamThemesDependency> Dependencies,
    bool Loading,
    string? Error);
