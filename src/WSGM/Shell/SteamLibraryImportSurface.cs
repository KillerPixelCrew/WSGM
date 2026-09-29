using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The Game Library's page inside Steam: one of its two surfaces.</summary>
public static class SteamLibraryImportSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.library-import";

    /// <summary>The route this page is served at.</summary>
    public const string Route = "/wsgm/library-import";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "library-import";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "libraryImport";

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "scan", "cancel", "toggleEntry", "select", "setMode", "cycleLaunch", "exclude", "include", "details",
        "openArtwork", "apply", "setSourceEnabled", "setCollections", "addFolder", "removeFolder", "setRoute",
        "cycleArtwork",
        "pickArtwork", "clearArtwork", "fillArtwork", "resetArtwork", "artworkOptions", "searchMatch", "setMatch"
    ];

    /// <summary>Installs the import renderer and its state subscription.</summary>
    /// <remarks>
    ///     The native components the page draws: Steam's fields, focusables, tabs and modals, and the
    ///     library item class map the toolkit's capsule is styled by. Steam's checkbox is wanted but
    ///     not required: the sidebar draws its toggle where a client has no checkbox.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-library-import-v3:steam-page",
        "Library import",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Modal,
            SteamPageProbe.ShowModal, SteamPageProbe.LibraryClasses, SteamPageProbe.Tabs
        ]);

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="revision">The model's revision, so an unchanged library is not serialized again.</param>
    /// <param name="backend">Answers user operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<GameLibraryState?>> read,
        Func<long> revision,
        IGameLibraryBackend backend,
        string id = "library-import")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, GameLibraryJsonContext.Default.GameLibraryState, revision)
            ],
            [
                SteamUiModuleBuilder.Command(PatchId, "scan", backend.ScanAsync),
                SteamUiModuleBuilder.Command(PatchId, "cancel", backend.CancelAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "toggleEntry", TryReadId,
                    backend.ToggleEntryAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<SelectRequest>(PatchId, "select", TryReadSelect,
                    (request, token) => backend.SelectAsync(request.Group, request.Query, request.Selected, token),
                    "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<ModeRequest>(PatchId, "setMode", TryReadMode,
                    (request, token) => backend.SetModeAsync(request.Id, request.Mode, request.Acknowledged, token),
                    "The import mode payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "cycleLaunch", TryReadId,
                    backend.CycleLaunchAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "exclude", TryReadId,
                    backend.ExcludeAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "include", TryReadId,
                    backend.IncludeAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "details", TryReadId,
                    backend.DetailsAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "openArtwork", TryReadId,
                    backend.OpenArtworkAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "apply", backend.ApplyAsync),
                SteamUiModuleBuilder.Command<SourceRequest>(PatchId, "setSourceEnabled", TryReadSource,
                    (request, token) => backend.SetSourceEnabledAsync(request.Id, request.Enabled, token),
                    "The source payload is invalid."),
                SteamUiModuleBuilder.Command<bool>(PatchId, "setCollections", TryReadCollections,
                    backend.SetCollectionsAsync, "The collections payload is invalid."),
                SteamUiModuleBuilder.Command<FolderRequest>(PatchId, "addFolder", TryReadFolder,
                    (request, token) => backend.AddFolderAsync(
                        request.Path, request.IncludeSubfolders, request.Extensions, token),
                    "The folder payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "removeFolder", TryReadId,
                    backend.RemoveFolderAsync, "The folder payload is invalid."),
                SteamUiModuleBuilder.Command<RouteRequest>(PatchId, "setRoute", TryReadRoute,
                    (request, token) => backend.SetRouteAsync(request.Id, request.Route, token),
                    "The route payload is invalid."),
                SteamUiModuleBuilder.Command<CycleRequest>(PatchId, "cycleArtwork", TryReadCycle,
                    (request, token) => backend.CycleArtworkAsync(request.Id, request.Asset, request.Delta, token),
                    "The artwork payload is invalid."),
                SteamUiModuleBuilder.Command<PickRequest>(PatchId, "pickArtwork", TryReadPick,
                    (request, token) => backend.PickArtworkAsync(request.Id, request.Asset, request.Url, token),
                    "The artwork payload is invalid."),
                SteamUiModuleBuilder.Command<AssetRequest>(PatchId, "clearArtwork", TryReadAsset,
                    (request, token) => backend.ClearArtworkAsync(request.Id, request.Asset, token),
                    "The artwork payload is invalid."),
                SteamUiModuleBuilder.Command<FillRequest>(PatchId, "fillArtwork", TryReadFill,
                    (request, token) => backend.FillArtworkAsync(
                        request.Preference, request.OnlyEmpty, request.Asset, token),
                    "The artwork payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "resetArtwork", backend.ResetArtworkAsync),
                SteamUiModuleBuilder.Command<AssetRequest>(PatchId, "artworkOptions", TryReadAsset,
                    (request, token) => backend.ArtworkOptionsAsync(request.Id, request.Asset, token),
                    "The artwork payload is invalid."),
                SteamUiModuleBuilder.Command<QueryRequest>(PatchId, "searchMatch", TryReadQuery,
                    (request, token) => backend.SearchMatchAsync(request.Id, request.Query, token),
                    "The search payload is invalid."),
                SteamUiModuleBuilder.Command<MatchRequest>(PatchId, "setMatch", TryReadMatch,
                    (request, token) => backend.SetMatchAsync(
                        request.Id, request.Provider, request.GameId, request.Name, token),
                    "The match payload is invalid.")
            ]);
    }

    private static bool TryReadId(JsonElement payload, out string value)
    {
        value = string.Empty;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadNonBlankString(payload, "id", out value);
    }

    /// <summary>Reads an id and one more bounded string, the shape most per-entry commands share.</summary>
    private static bool TryReadIdAnd(
        JsonElement payload, string name, int maximum, int properties, out string id, out string value)
    {
        value = string.Empty;
        id = string.Empty;
        return SteamUiPayload.HasExactly(payload, properties)
               && SteamUiPayload.TryReadNonBlankString(payload, "id", out id)
               && SteamUiPayload.TryReadNonBlankString(payload, name, out value);
    }

    private static bool TryReadSelect(JsonElement payload, out SelectRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadString(payload, "group", out var group)
            || !SteamUiPayload.TryReadString(payload, "query", out var query)
            || !SteamUiPayload.TryReadBoolean(payload, "selected", out var selected))
        {
            return false;
        }

        value = new SelectRequest(group, query, selected);
        return true;
    }

    private static bool TryReadMode(JsonElement payload, out ModeRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "mode", 32, 3, out var id, out var mode)
            || !SteamUiPayload.TryReadBoolean(payload, "acknowledged", out var acknowledged))
        {
            return false;
        }

        value = new ModeRequest(id, mode, acknowledged);
        return true;
    }

    private static bool TryReadCollections(JsonElement payload, out bool enabled)
    {
        return SteamUiPayload.TryReadOnlyBoolean(payload, "enabled", out enabled);
    }

    private static bool TryReadSource(JsonElement payload, out SourceRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !SteamUiPayload.TryReadBoolean(payload, "enabled", out var enabled))
        {
            return false;
        }

        value = new SourceRequest(id, enabled);
        return true;
    }

    private static bool TryReadFolder(JsonElement payload, out FolderRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.TryReadBoolean(payload, "includeSubfolders", out var includeSubfolders)
            || !SteamUiPayload.TryReadStrings(payload, "extensions", out var extensions))
        {
            return false;
        }

        value = new FolderRequest(path, includeSubfolders, extensions);
        return true;
    }

    private static bool TryReadRoute(JsonElement payload, out RouteRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "route", 32, 2, out var id, out var route))
        {
            return false;
        }

        value = new RouteRequest(id, route);
        return true;
    }

    private static bool TryReadCycle(JsonElement payload, out CycleRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "asset", 8, 3, out var id, out var asset)
            || !SteamUiPayload.TryReadInt(payload, "delta", -1, 1, out var delta)
            || delta == 0)
        {
            return false;
        }

        value = new CycleRequest(id, asset, delta);
        return true;
    }

    private static bool TryReadPick(JsonElement payload, out PickRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "asset", 8, 3, out var id, out var asset)
            || !SteamUiPayload.TryReadNonBlankString(payload, "url", out var url))
        {
            return false;
        }

        value = new PickRequest(id, asset, url);
        return true;
    }

    private static bool TryReadAsset(JsonElement payload, out AssetRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "asset", 8, 2, out var id, out var asset))
        {
            return false;
        }

        value = new AssetRequest(id, asset);
        return true;
    }

    private static bool TryReadFill(JsonElement payload, out FillRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "preference", out var preference)
            || !SteamUiPayload.TryReadBoolean(payload, "onlyEmpty", out var onlyEmpty)
            || !SteamUiPayload.TryReadString(payload, "asset", out var asset))
        {
            return false;
        }

        value = new FillRequest(preference, onlyEmpty, asset);
        return true;
    }

    private static bool TryReadQuery(JsonElement payload, out QueryRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !SteamUiPayload.TryReadString(payload, "query", out var query))
        {
            return false;
        }

        value = new QueryRequest(id, query);
        return true;
    }

    private static bool TryReadMatch(JsonElement payload, out MatchRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 4)
            || !SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !SteamUiPayload.TryReadString(payload, "provider", out var provider)
            || !SteamUiPayload.TryReadString(payload, "gameId", out var gameId)
            || !SteamUiPayload.TryReadString(payload, "name", out var name))
        {
            return false;
        }

        value = new MatchRequest(id, provider, gameId, name);
        return true;
    }

    /// <summary>One request to select or deselect what a tab and search show.</summary>
    private readonly record struct SelectRequest(string Group, string Query, bool Selected);

    /// <summary>One request to change an entry's launch mode.</summary>
    private readonly record struct ModeRequest(string Id, string Mode, bool Acknowledged);

    /// <summary>One request to tick or untick a source.</summary>
    private readonly record struct SourceRequest(string Id, bool Enabled);

    /// <summary>One request to add a shortcuts folder.</summary>
    private readonly record struct FolderRequest(string Path, bool IncludeSubfolders, IReadOnlyList<string> Extensions);

    /// <summary>One request to change an entry's command route.</summary>
    private readonly record struct RouteRequest(string Id, string Route);

    /// <summary>One request to cycle an entry's artwork.</summary>
    private readonly record struct CycleRequest(string Id, string Asset, int Delta);

    /// <summary>One request to pick an image.</summary>
    private readonly record struct PickRequest(string Id, string Asset, string Url);

    /// <summary>One request naming an entry's artwork type.</summary>
    private readonly record struct AssetRequest(string Id, string Asset);

    /// <summary>One request to fill the selected entries' artwork.</summary>
    private readonly record struct FillRequest(string Preference, bool OnlyEmpty, string Asset);

    /// <summary>One search for an entry's game.</summary>
    private readonly record struct QueryRequest(string Id, string Query);

    /// <summary>One request to match an entry to a provider's game.</summary>
    private readonly record struct MatchRequest(string Id, string Provider, string GameId, string Name);
}
