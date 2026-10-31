using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The complete host-owned model for a Steam-native artwork browser.</summary>
/// <param name="AppId">Steam application or shortcut whose artwork is being edited.</param>
/// <param name="AppName">Displayed Steam game name.</param>
/// <param name="Tabs">Currently available artwork and management tabs.</param>
/// <param name="ActiveTab">Id of the selected tab.</param>
/// <param name="Assets">Current community results; their opaque ids expire when the result set changes.</param>
/// <param name="OfficialAssets">Current official Steam results with their own opaque ids.</param>
/// <param name="ManagedSlots">Current custom-artwork presence and previews.</param>
/// <param name="Filter">Selected result-filter values.</param>
/// <param name="GameMatches">Currently authorized manual provider matches.</param>
/// <param name="SelectedGame">Display name of the manual match, or null when using the Steam app id.</param>
/// <param name="Page">Zero-based provider result page most recently loaded.</param>
/// <param name="Loading">Whether the current result request is still running.</param>
/// <param name="HasMore">Whether another provider result page may be requested.</param>
/// <param name="Notice">Non-error status text, or null.</param>
/// <param name="Error">Current refusal or failure text, or null; takes visual precedence over Notice.</param>
/// <param name="Revision">Monotonic observation revision for publication deduplication.</param>
internal sealed record SteamArtworkBrowserState(
    uint AppId,
    string AppName,
    IReadOnlyList<SteamArtworkBrowserTab> Tabs,
    string ActiveTab,
    IReadOnlyList<SteamArtworkBrowserAsset> Assets,
    IReadOnlyList<SteamArtworkOfficialAsset> OfficialAssets,
    IReadOnlyList<SteamArtworkManagedSlot> ManagedSlots,
    SteamArtworkBrowserFilter Filter,
    IReadOnlyList<SteamArtworkBrowserGame> GameMatches,
    string? SelectedGame = null,
    int Page = 0,
    bool Loading = false,
    bool HasMore = false,
    string? Notice = null,
    string? Error = null,
    long Revision = 0);
