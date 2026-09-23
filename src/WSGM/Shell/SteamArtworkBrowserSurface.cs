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

    /// <summary>Applies a locally selected image.</summary>
    Task<SteamUiCommandResult> ApplyLocalAsync(
        string tab, string name, string base64, CancellationToken cancellationToken);

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

    /// <summary>The exact command vocabulary emitted by the page.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "selectTab", "apply", "applyOfficial", "clear", "loadMore", "applyLocal", "setFilter", "searchGames",
        "selectGame", "saveLogoPosition", "resetLogoPosition"
    ];

    /// <summary>Installs the artwork renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.artwork-browser",
        "artworkBrowser",
        "steam-artwork-browser-v2:native-steam-components",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_artwork_browser_probe_")}}
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}}),
              controls:count({{SteamUiProbeJs.NativeFieldTokens}}),
              tabs:count({{SteamUiProbeJs.NativeTabsTokens}}),
              modal:count({{SteamUiProbeJs.NativeModalTokens}}),
              showModal:count({{SteamUiProbeJs.NativeShowModalTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.IsOne(root, "react")
                && SteamUiPatchEvaluation.IsOne(root, "focusable")
                && SteamUiPatchEvaluation.IsOne(root, "controls")
                && SteamUiPatchEvaluation.IsOne(root, "tabs")
                && SteamUiPatchEvaluation.IsOne(root, "modal")
                && SteamUiPatchEvaluation.IsOne(root, "showModal"),
        "status.installed&&status.resolved&&status.subscribed",
        "!status.installed",
        "Artwork browser");

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
                SteamUiModuleBuilder.Command<(string Tab, string Name, string Base64)>(
                    PatchId, "applyLocal", TryReadLocal,
                    (value, token) => backend.ApplyLocalAsync(value.Tab, value.Name, value.Base64, token),
                    "The local artwork payload is invalid."),
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

    private static bool TryReadLocal(
        JsonElement payload,
        out (string Tab, string Name, string Base64) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "tab", 32, out var tab)
            || !SteamUiPayload.TryReadBoundedString(payload, "name", 260, out var name)
            || !SteamUiPayload.TryReadBoundedString(payload, "base64", 24 * 1024 * 1024, out var base64)
            || !SteamUiPayload.HasExactly(payload, 3))
        {
            return false;
        }

        value = (tab, name, base64);
        return true;
    }

    private static bool TryReadFilter(JsonElement payload, out SteamArtworkBrowserFilter filter)
    {
        filter = default!;
        if (!TryReadStrings(payload, "styles", 16, out var styles)
            || !TryReadStrings(payload, "dimensions", 64, out var dimensions)
            || !TryReadStrings(payload, "mimes", 8, out var mimes)
            || !TryReadBoolean(payload, "static", out var includeStatic)
            || !TryReadBoolean(payload, "animated", out var animated)
            || !TryReadBoolean(payload, "adult", out var adult)
            || !TryReadBoolean(payload, "humor", out var humor)
            || !TryReadBoolean(payload, "epilepsy", out var epilepsy)
            || !TryReadBoolean(payload, "untagged", out var untagged)
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
        if (!payload.TryGetProperty("id", out var property) || !SteamUiPayload.HasExactly(payload, 1))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        id = property.GetString();
        return id is { Length: > 0 and <= 128 };
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

    private static bool TryReadStrings(
        JsonElement payload, string name, int maximum, out IReadOnlyList<string> values)
    {
        values = [];
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> parsed = [];
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 64 } value
                                                       || parsed.Count >= maximum)
            {
                return false;
            }

            parsed.Add(value);
        }

        values = parsed;
        return true;
    }

    private static bool TryReadBoolean(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamArtworkBrowserState))]
[JsonSerializable(typeof(SteamPageState))]
internal sealed partial class ArtworkJsonContext : JsonSerializerContext;
