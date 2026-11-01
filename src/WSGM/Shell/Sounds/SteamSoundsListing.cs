namespace WSGM.Shell;

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
