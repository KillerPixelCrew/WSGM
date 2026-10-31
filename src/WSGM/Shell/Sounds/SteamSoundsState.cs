using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One installed sound pack as the UI sounds page and the Customization hub show it.</summary>
/// <param name="Id">The library id, sent back with every command.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The manifest's author, or empty.</param>
/// <param name="Version">The manifest's version, or empty.</param>
/// <param name="Description">The manifest's description, or empty.</param>
/// <param name="StoreId">The DeckThemes listing it came from, or null for a local archive.</param>
/// <param name="Source">Repository or source text, or empty.</param>
/// <param name="Error">Why the pack cannot be used, or null.</param>
/// <param name="Compatibility">Its coverage of the current Steam resources, or null before a comparison.</param>
/// <param name="Mapped">The Steam resources it replaces.</param>
/// <param name="Defaults">The Steam resources that keep Steam's sound, missing or ignored.</param>
/// <param name="Problems">Missing, unsupported or unknown entries worth reading.</param>
/// <param name="Assets">The files it can preview, relative to the pack.</param>
internal sealed record SteamSoundsPack(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string? StoreId,
    string Source,
    string? Error,
    string? Compatibility,
    IReadOnlyList<string> Mapped,
    IReadOnlyList<string> Defaults,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Assets);

/// <summary>One DeckThemes Audio Loader listing.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">What the store shows.</param>
/// <param name="Author">The author as the pack names them.</param>
/// <param name="Version">The store's version text.</param>
/// <param name="ImageUrl">Its first screenshot, or null.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Stars">How often it was starred.</param>
/// <param name="Updated">When it was last updated, as a date, or null.</param>
/// <param name="InstalledVersion">The installed copy's version, or null when it is not installed.</param>
internal sealed record SteamSoundsListing(
    string Id,
    string Name,
    string Author,
    string Version,
    string? ImageUrl,
    int Downloads,
    int Stars,
    string? Updated,
    string? InstalledVersion);

/// <summary>The store page last asked for.</summary>
/// <param name="Items">The listings on that page.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The page, from one, or zero before the store was asked.</param>
/// <param name="PageSize">How many listings a page holds.</param>
internal sealed record SteamSoundsBrowse(IReadOnlyList<SteamSoundsListing> Items, int Total, int Page, int PageSize);

/// <summary>Everything the UI sounds page and the Customization hub draw.</summary>
/// <param name="Selected">The selected pack's id, empty for Steam's own sounds.</param>
/// <param name="Packs">The installed packs.</param>
/// <param name="Browse">The store page last asked for.</param>
/// <param name="Busy">Whether an operation is running.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Compatibility">What the selection does to Steam's sounds.</param>
/// <param name="Integration">What Steam's sound integration is doing.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamSoundsState(
    string Selected,
    IReadOnlyList<SteamSoundsPack> Packs,
    SteamSoundsBrowse Browse,
    bool Busy,
    string? Error,
    string Compatibility,
    string Integration,
    long Revision);
