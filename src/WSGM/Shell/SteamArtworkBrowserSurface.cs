using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>One artwork result rendered by Steam's native artwork browser page.</summary>
public sealed record SteamArtworkBrowserAsset(
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
public sealed record SteamArtworkBrowserTab(string Id, string Label, bool Manage = false);

/// <summary>Current custom artwork for one manageable slot.</summary>
public sealed record SteamArtworkManagedSlot(string Id, string Label, bool HasCustomArtwork, string? ImageUrl = null);

/// <summary>One official Steam asset offered alongside community artwork.</summary>
public sealed record SteamArtworkOfficialAsset(
    string Id,
    string Label,
    string ImageUrl,
    int Width,
    int Height,
    string Format);

/// <summary>The active artwork filters, matching SteamGridDB's public query vocabulary.</summary>
public sealed record SteamArtworkBrowserFilter(
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
public sealed record SteamArtworkBrowserGame(string Id, string Name, string Provider);

/// <summary>The complete host-owned model for a Steam-native artwork browser.</summary>
public sealed record SteamArtworkBrowserState(
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

/// <summary>Answers explicit operations from the artwork page.</summary>
public interface ISteamArtworkBrowserBackend
{
    /// <summary>Selects and loads one published tab.</summary>
    Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Applies one currently published opaque result.</summary>
    Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies one currently published official Steam asset.</summary>
    Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken);

    /// <summary>Clears custom artwork from one published slot.</summary>
    Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Loads another result page when the provider reports one.</summary>
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
    Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken);

    /// <summary>Searches configured providers for a manual game override.</summary>
    Task<SteamUiCommandResult> SearchGamesAsync(string term, CancellationToken cancellationToken);

    /// <summary>Selects an opaque game match, or clears it when the id is null.</summary>
    Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken);

    /// <summary>Saves a custom Steam logo position.</summary>
    Task<SteamUiCommandResult> SaveLogoPositionAsync(
        string anchor, int width, int height, CancellationToken cancellationToken);

    /// <summary>Clears Steam's custom logo position.</summary>
    Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken);
}

/// <summary>A reusable controller-native artwork browser registered as a Steam route.</summary>
public static class SteamArtworkBrowserSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.artwork-browser";

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
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="backend">Answers user operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
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
        return SteamUiPayload.TryReadBoundedString(payload, "tab", 32, out tab)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadBoundedString(payload, "id", 128, out id)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    /// <remarks>
    ///     A path rather than the image: requests from the page are held to a few kilobytes, and the
    ///     file was chosen in Steam's own picker, so the host reads it where it lies.
    /// </remarks>
    private static bool TryReadLocal(JsonElement payload, out (string Tab, string Path) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "tab", 32, out var tab)
            || !SteamUiPayload.TryReadBoundedString(payload, "path", 1024, out var path)
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
        if (!SteamUiPayload.TryReadStrings(payload, "styles", 16, 64, out var styles)
            || !SteamUiPayload.TryReadStrings(payload, "dimensions", 64, 64, out var dimensions)
            || !SteamUiPayload.TryReadStrings(payload, "mimes", 8, 64, out var mimes)
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
        return SteamUiPayload.TryReadBoundedString(payload, "term", 200, out term)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryReadOptionalId(JsonElement payload, out string? id)
    {
        id = null;
        return SteamUiPayload.HasExactly(payload, 1) &&
               SteamUiPayload.TryReadNullableString(payload, "id", 128, out id);
    }

    private static bool TryReadLogoPosition(
        JsonElement payload,
        out (string Anchor, int Width, int Height) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "anchor", 24, out var anchor)
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
