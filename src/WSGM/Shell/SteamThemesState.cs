using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One installed theme as the Themes page and the overlay show it.</summary>
/// <param name="Id">The store id, or the name.</param>
/// <param name="Name">The identity among installed themes, sent back with every command.</param>
/// <param name="DisplayName">The name the user sees.</param>
/// <param name="Version">The installed version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="Hidden">Whether it is kept off the Quick Access section.</param>
/// <param name="Status">
///     <c>installed</c>, <c>outdated</c>, <c>local</c> (the store does not list it) or <c>unknown</c>
///     (not checked yet).
/// </param>
/// <param name="LatestVersion">The store's version when it differs, or null.</param>
/// <param name="Patches">Its patches and their values.</param>
/// <param name="Dependencies">The names of the themes it needs.</param>
internal sealed record SteamThemesInstalled(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    bool Enabled,
    bool Hidden,
    string Status,
    string? LatestVersion,
    IReadOnlyList<ThemePatchSnapshot> Patches,
    IReadOnlyList<string> Dependencies);

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

/// <summary>A theme another needs, as the detail view lists it.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Installed">Whether it is already installed.</param>
internal sealed record SteamThemesDependency(string Id, string Name, string DisplayName, bool Installed);

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

/// <summary>The Settings tab.</summary>
/// <param name="Enabled">Whether enabled themes are installed into Steam.</param>
/// <param name="TranslationsBranch">Which translation table is fetched.</param>
/// <param name="SteamBeta">Whether Steam is on a beta branch.</param>
/// <param name="Translations">How many class names the table maps.</param>
/// <param name="TranslationsFetched">When the table was last fetched, or null.</param>
/// <param name="ThemesPath">The themes folder.</param>
/// <param name="SteamLink">What became of Steam's <c>themes_custom</c> link.</param>
internal sealed record SteamThemesSettings(
    bool Enabled,
    string TranslationsBranch,
    bool SteamBeta,
    int Translations,
    string? TranslationsFetched,
    string ThemesPath,
    string SteamLink);

/// <summary>The whole model behind the Themes page, the Quick Access section and the overlay.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>installed</c>, <c>profiles</c> or <c>settings</c>.</param>
/// <param name="Themes">The installed themes that are not profiles, by display name.</param>
/// <param name="Presets">The profiles.</param>
/// <param name="SelectedPreset">The enabled profile's name, or empty.</param>
/// <param name="Browse">The Browse tab.</param>
/// <param name="Detail">The theme opened from the store, or null.</param>
/// <param name="Settings">The Settings tab.</param>
/// <param name="Errors">The folders the loader refused.</param>
/// <param name="Busy">Whether an install, update or reload is running.</param>
/// <param name="Notice">Something the user should read, or null.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Updates">How many installed themes the store has a newer version of.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamThemesState(
    string ActiveTab,
    IReadOnlyList<SteamThemesInstalled> Themes,
    IReadOnlyList<SteamThemesInstalled> Presets,
    string SelectedPreset,
    SteamThemesBrowse Browse,
    SteamThemesDetail? Detail,
    SteamThemesSettings Settings,
    IReadOnlyList<ThemeLoadError> Errors,
    bool Busy,
    string? Notice,
    string? Error,
    int Updates,
    long Revision);
