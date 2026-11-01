using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One store listing as the page draws it.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The store's version text.</param>
/// <param name="Target">The part of Steam it restyles.</param>
/// <param name="Targets">Every target the store tags it with.</param>
/// <param name="Author">The author as the theme names them.</param>
/// <param name="ImageUrl">Its first screenshot, or null.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Stars">How often it was starred.</param>
/// <param name="Updated">When it was last updated, as a date, or null.</param>
/// <param name="LocalStatus"><c>none</c>, <c>installed</c> or <c>outdated</c>.</param>
internal sealed record SteamThemesStoreItem(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Target,
    IReadOnlyList<string> Targets,
    string Author,
    string? ImageUrl,
    int Downloads,
    int Stars,
    string? Updated,
    string LocalStatus);
