using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>One artwork result rendered by Steam's native artwork browser page.</summary>
/// <param name="Id">Opaque current-result identity accepted by ApplyAsync; it is not a provider URL.</param>
/// <param name="ImageUrl">Provider image URL used for preview and host download.</param>
/// <param name="ThumbnailUrl">Preview URL, falling back to the full image URL when the provider has no thumbnail.</param>
/// <param name="Width">Provider-reported image width in pixels.</param>
/// <param name="Height">Provider-reported image height in pixels.</param>
/// <param name="Format">Provider file extension or format label.</param>
/// <param name="Provider">Provider display name.</param>
/// <param name="Author">Creator attribution, or null when unavailable.</param>
/// <param name="Style">Provider style tag, or null.</param>
/// <param name="Notes">Provider notes, or null.</param>
/// <param name="Animated">Whether the provider marks this image animated.</param>
/// <param name="Nsfw">Whether the provider marks this image as adult content.</param>
/// <param name="Humor">Whether the provider marks this image as humor.</param>
/// <param name="Epilepsy">Whether the provider marks this image as containing flashing imagery.</param>
internal sealed record SteamArtworkBrowserAsset(
    string Id,
    string ImageUrl,
    string ThumbnailUrl,
    int Width,
    int Height,
    string Format,
    string Provider,
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);

/// <summary>One artwork slot shown in the page's Decky-compatible tab strip.</summary>
/// <param name="Id">Stable artwork-slot id, or the management-tab id.</param>
/// <param name="Label">Displayed tab label.</param>
/// <param name="Manage">True for the management tab, which does not fetch provider results.</param>
internal sealed record SteamArtworkBrowserTab(string Id, string Label, bool Manage = false);

/// <summary>Current custom artwork for one manageable slot.</summary>
/// <param name="Id">Stable artwork-slot id accepted by clear operations.</param>
/// <param name="Label">Displayed slot name.</param>
/// <param name="HasCustomArtwork">Whether a custom file currently exists for this slot.</param>
/// <param name="ImageUrl">Preview data URL, or null when absent, unreadable or too large.</param>
internal sealed record SteamArtworkManagedSlot(string Id, string Label, bool HasCustomArtwork, string? ImageUrl = null);

/// <summary>One official Steam asset offered alongside community artwork.</summary>
/// <param name="Id">Opaque current official-result identity accepted by ApplyOfficialAsync.</param>
/// <param name="Label">Displayed description of the official image.</param>
/// <param name="ImageUrl">Official Steam image URL.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Format">Image file extension or format label.</param>
internal sealed record SteamArtworkOfficialAsset(
    string Id,
    string Label,
    string ImageUrl,
    int Width,
    int Height,
    string Format);

/// <summary>The active artwork filters, matching SteamGridDB's public query vocabulary.</summary>
/// <param name="Styles">Provider style tags to include; an empty list imposes no style restriction.</param>
/// <param name="Dimensions">Provider dimension tokens to include; an empty list imposes no dimension restriction.</param>
/// <param name="Mimes">Allowed MIME types; an empty list imposes no MIME restriction.</param>
/// <param name="Static">Whether static images are included; Static or Animated must be true.</param>
/// <param name="Animated">Whether animated images are included.</param>
/// <param name="Adult">Whether adult-tagged images are included.</param>
/// <param name="Humor">Whether humor-tagged images are included.</param>
/// <param name="Epilepsy">Whether flashing-imagery-tagged images are included.</param>
/// <param name="Untagged">Whether images without content tags are included.</param>
internal sealed record SteamArtworkBrowserFilter(
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Mimes,
    bool Static = true,
    bool Animated = true,
    bool Adult = false,
    bool Humor = true,
    bool Epilepsy = true,
    bool Untagged = true);

/// <summary>One host-authorized game override returned by an artwork provider.</summary>
/// <param name="Id">Opaque identity for a currently authorized provider match.</param>
/// <param name="Name">Displayed game name.</param>
/// <param name="Provider">Provider display name, falling back to its configured id.</param>
internal sealed record SteamArtworkBrowserGame(string Id, string Name, string Provider);

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

/// <summary>Answers artwork operations shared by Steam pages and overlay browse sessions.</summary>
/// <remarks>Accepted background reads and writes use the owner lifetime; observe published state for their final outcome.</remarks>
internal interface ISteamArtworkBrowserBackend
{
    /// <summary>Selects and loads one published tab.</summary>
    /// <param name="tab">Id of a currently configured artwork or management tab.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the tab change was accepted; provider loading continues through published state.</returns>
    Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Applies one currently published opaque result.</summary>
    /// <param name="id">Opaque id from the current Assets publication.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the write was accepted; its final result is published in the page state.</returns>
    Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies one currently published official Steam asset.</summary>
    /// <param name="id">Opaque id from the current OfficialAssets publication.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the official-artwork write was accepted; its final result is published.</returns>
    Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken);

    /// <summary>Clears custom artwork from one published slot.</summary>
    /// <param name="tab">Published artwork-slot id whose custom image should be removed.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the clear operation was accepted, or why the slot could not be changed.</returns>
    Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Loads another result page when the provider reports one.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether another result load was accepted; rejects while loading or when no further page is available.</returns>
    Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken);

    /// <summary>Applies an image file the user chose with Steam's file picker.</summary>
    /// <param name="tab">The slot.</param>
    /// <param name="path">The file's full local path.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the apply was started, or why not.</returns>
    Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path, CancellationToken cancellationToken);

    /// <summary>Applies a transparent image to one slot, so the slot shows nothing.</summary>
    /// <param name="tab">The slot; every slot but the icon.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the apply was started, or why not.</returns>
    Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Replaces the active filter and reloads the current tab.</summary>
    /// <param name="filter">Replacement filters for the active result tab; at least one of Static or Animated must be enabled.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the reload was accepted; old result identities are invalidated.</returns>
    Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken);

    /// <summary>Searches configured providers for a manual game override.</summary>
    /// <param name="term">Nonblank manual game-search text.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the provider search; matches and errors arrive in published state.</returns>
    Task<SteamUiCommandResult> SearchGamesAsync(string term, CancellationToken cancellationToken);

    /// <summary>Selects an opaque game match, or clears it when the id is null.</summary>
    /// <param name="id">Opaque current GameMatches identity, or null to restore lookup by Steam app id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the match change and result reload were accepted.</returns>
    Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken);

    /// <summary>Saves a custom Steam logo position.</summary>
    /// <param name="anchor">Steam anchor such as TopLeft, CenterCenter or BottomRight.</param>
    /// <param name="width">Logo width percentage, from 5 through 100.</param>
    /// <param name="height">Logo height percentage, from 5 through 100.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the Steam write was accepted; its final result is published.</returns>
    Task<SteamUiCommandResult> SaveLogoPositionAsync(
        string anchor, int width, int height, CancellationToken cancellationToken);

    /// <summary>Clears Steam's custom logo position.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether restoring Steam's default logo position was accepted.</returns>
    Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken);
}

/// <summary>A reusable controller-native artwork browser registered as a Steam route.</summary>
internal static class SteamArtworkBrowserSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "wsgm.artwork-browser";

    /// <summary>The Steam router pattern registered by this surface.</summary>
    public const string Route = "/wsgm/artwork/:appid";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "artworkBrowser";

    /// <summary>The exact command vocabulary emitted by the page.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "selectTab", "apply", "applyOfficial", "clear", "loadMore", "applyLocal", "applyInvisible", "setFilter",
        "searchGames", "selectGame", "saveLogoPosition", "resetLogoPosition"
    ];

    /// <summary>Installs the artwork renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-artwork-browser-v3:steam-page",
        "Artwork browser",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Tabs,
            SteamPageProbe.Modal, SteamPageProbe.ShowModal
        ]);

    /// <summary>The concrete route that opens this page for one game.</summary>
    /// <param name="appId">The game the page should open on.</param>
    /// <returns>The path Steam's router navigates to.</returns>
    public static string RouteFor(uint appId)
    {
        return "/wsgm/artwork/" + appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Declares the page route, artwork state, and exact backend commands.</summary>
    /// <param name="enabled">Whether state may be published; patch installation is coordinated separately.</param>
    /// <param name="read">Reads the current model; null skips this publication without retracting the previous state.</param>
    /// <param name="backend">Answers user operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing the backend and readers; constructing it does not install the patch.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamArtworkBrowserState?>> read,
        ISteamArtworkBrowserBackend backend,
        string id = "artwork-browser")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId,
                    enabled,
                    read,
                    ArtworkJsonContext.Default.SteamArtworkBrowserState)
            ],
            [
                SteamUiModuleBuilder.Command<string>(PatchId, "selectTab", TryReadTab,
                    backend.SelectTabAsync, "The artwork tab payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "apply", TryReadId,
                    backend.ApplyAsync, "The artwork selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "applyOfficial", TryReadId,
                    backend.ApplyOfficialAsync, "The official artwork selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "clear", TryReadTab,
                    backend.ClearAsync, "The artwork reset payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "loadMore", backend.LoadMoreAsync),
                SteamUiModuleBuilder.Command<(string Tab, string Path)>(
                    PatchId, "applyLocal", TryReadLocal,
                    (value, token) => backend.ApplyLocalAsync(value.Tab, value.Path, token),
                    "The local artwork payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "applyInvisible", TryReadTab,
                    backend.ApplyInvisibleAsync, "The artwork slot payload is invalid."),
                SteamUiModuleBuilder.Command<SteamArtworkBrowserFilter>(
                    PatchId, "setFilter", TryReadFilter, backend.SetFilterAsync,
                    "The artwork filter payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "searchGames", TryReadTerm,
                    backend.SearchGamesAsync, "The game search payload is invalid."),
                SteamUiModuleBuilder.Command<string?>(PatchId, "selectGame", TryReadOptionalId,
                    backend.SelectGameAsync, "The game selection payload is invalid."),
                SteamUiModuleBuilder.Command<(string Anchor, int Width, int Height)>(
                    PatchId, "saveLogoPosition", TryReadLogoPosition,
                    (value, token) => backend.SaveLogoPositionAsync(
                        value.Anchor, value.Width, value.Height, token),
                    "The logo position payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "resetLogoPosition", backend.ResetLogoPositionAsync)
            ]);
    }

    private static bool TryReadTab(JsonElement payload, out string tab)
    {
        return SteamUiPayload.TryReadNonBlankString(payload, "tab", out tab)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadNonBlankString(payload, "id", out id)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    // The host reads the chosen file locally; image bytes never travel in the page command.
    private static bool TryReadLocal(JsonElement payload, out (string Tab, string Path) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadNonBlankString(payload, "tab", out var tab)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (tab, path);
        return true;
    }

    private static bool TryReadFilter(JsonElement payload, out SteamArtworkBrowserFilter filter)
    {
        filter = default!;
        if (!SteamUiPayload.TryReadStrings(payload, "styles", out var styles)
            || !SteamUiPayload.TryReadStrings(payload, "dimensions", out var dimensions)
            || !SteamUiPayload.TryReadStrings(payload, "mimes", out var mimes)
            || !SteamUiPayload.TryReadBoolean(payload, "static", out var includeStatic)
            || !SteamUiPayload.TryReadBoolean(payload, "animated", out var animated)
            || !SteamUiPayload.TryReadBoolean(payload, "adult", out var adult)
            || !SteamUiPayload.TryReadBoolean(payload, "humor", out var humor)
            || !SteamUiPayload.TryReadBoolean(payload, "epilepsy", out var epilepsy)
            || !SteamUiPayload.TryReadBoolean(payload, "untagged", out var untagged)
            || !SteamUiPayload.HasExactly(payload, 9))
        {
            return false;
        }

        filter = new SteamArtworkBrowserFilter(
            styles, dimensions, mimes, includeStatic, animated, adult, humor, epilepsy, untagged);
        return includeStatic || animated;
    }

    private static bool TryReadTerm(JsonElement payload, out string term)
    {
        return SteamUiPayload.TryReadNonBlankString(payload, "term", out term)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryReadOptionalId(JsonElement payload, out string? id)
    {
        id = null;
        return SteamUiPayload.HasExactly(payload, 1) &&
               SteamUiPayload.TryReadNullableString(payload, "id", out id);
    }

    private static bool TryReadLogoPosition(
        JsonElement payload,
        out (string Anchor, int Width, int Height) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadNonBlankString(payload, "anchor", out var anchor)
            || !SteamUiPayload.TryReadInt(payload, "width", 5, 100, out var width)
            || !SteamUiPayload.TryReadInt(payload, "height", 5, 100, out var height)
            || !SteamUiPayload.HasExactly(payload, 3)
            || anchor is not ("TopLeft" or "TopCenter" or "TopRight" or "CenterLeft" or "CenterCenter"
                or "CenterRight" or "BottomLeft" or "BottomCenter" or "BottomRight"))
        {
            return false;
        }

        value = (anchor, width, height);
        return true;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamArtworkBrowserState))]
internal sealed partial class ArtworkJsonContext : JsonSerializerContext;
