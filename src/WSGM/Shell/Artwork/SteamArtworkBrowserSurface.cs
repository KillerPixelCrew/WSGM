using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

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
        SteamUiBrowseCommands.SetTab, SteamUiBrowseCommands.More, "apply", "applyOfficial", "clear", "applyLocal",
        "applyInvisible", "setFilter", "searchGames", "selectGame", "saveLogoPosition", "resetLogoPosition"
    ];

    /// <summary>Every tab the page can show: each artwork slot and the manage tab.</summary>
    private static readonly string[] Tabs =
        [.. ArtworkAssetNames.Ordered.Select(slot => slot.Id), ArtworkBrowserTabs.ManageTab];

    /// <summary>Installs the artwork renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-artwork-browser-v3:steam-page",
        "Artwork browser");

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
                .. SteamUiModuleBuilder.BrowseCommands(PatchId, new SteamUiBrowseBindings
                {
                    Subject = "artwork",
                    Tabs = Tabs,
                    SetTab = backend.SelectTabAsync,
                    More = backend.LoadMoreAsync
                }),
                SteamUiModuleBuilder.Command<string>(PatchId, "apply", TryReadId,
                    backend.ApplyAsync, "The artwork selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "applyOfficial", TryReadId,
                    backend.ApplyOfficialAsync, "The official artwork selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "clear", TryReadTab,
                    backend.ClearAsync, "The artwork reset payload is invalid."),
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
