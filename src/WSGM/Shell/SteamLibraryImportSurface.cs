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

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "scan", "cancel", "toggleEntry", "selectAll", "setMode", "exclude", "include", "openArtwork", "apply",
        "setSourceEnabled", "addFolder", "removeFolder", "setRoute", "cycleArtwork", "pickArtwork",
        "clearArtwork", "fillArtwork", "resetArtwork", "artworkOptions", "searchMatch", "setMatch"
    ];

    /// <summary>Installs the import renderer and its state subscription.</summary>
    /// <remarks>
    ///     The native components the page draws: Steam's fields, focusables, tabs and modals, and the
    ///     library item class map the toolkit's capsule is styled by. Requiring a component that is
    ///     never rendered would make the gate refuse over something that does not matter, so Steam's
    ///     checkbox is wanted but not required.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "libraryImport",
        "steam-library-import-v2:native-steam-components+library-classes+tabs",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_library_import_probe_")}}
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}}),
              controls:count({{SteamUiProbeJs.NativeFieldTokens}}),
              modal:count({{SteamUiProbeJs.NativeModalTokens}}),
              showModal:count({{SteamUiProbeJs.NativeShowModalTokens}}),
              classes:count(['ControllerSupportIcon:"','LibraryItemIcons:"','LibraryItemBox:"']),
              tabs:count(['.TabRowTabs','activeTab:'])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.IsOne(root, "react")
                && SteamUiPatchEvaluation.IsOne(root, "focusable")
                && SteamUiPatchEvaluation.IsOne(root, "controls")
                && SteamUiPatchEvaluation.IsOne(root, "modal")
                && SteamUiPatchEvaluation.IsOne(root, "showModal")
                && SteamUiPatchEvaluation.IsOne(root, "classes")
                && SteamUiPatchEvaluation.IsOne(root, "tabs"),
        "status.installed&&status.resolved&&status.subscribed",
        "!status.installed",
        "Library import");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="backend">Answers user operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<GameLibraryState?>> read,
        IGameLibraryBackend backend,
        string id = "library-import")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, GameLibraryJsonContext.Default.GameLibraryState)
            ],
            [
                SteamUiModuleBuilder.Command(PatchId, "scan", backend.ScanAsync),
                SteamUiModuleBuilder.Command(PatchId, "cancel", backend.CancelAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "toggleEntry", TryReadId,
                    backend.ToggleEntryAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<bool>(PatchId, "selectAll", TryReadSelected,
                    backend.SelectAllAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<ModeRequest>(PatchId, "setMode", TryReadMode,
                    (request, token) => backend.SetModeAsync(
                        request.Id, request.Mode, request.Acknowledged, token),
                    "The import mode payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "exclude", TryReadId,
                    backend.ExcludeAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "include", TryReadId,
                    backend.IncludeAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "openArtwork", TryReadId,
                    backend.OpenArtworkAsync, "The import selection payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "apply", backend.ApplyAsync),
                SteamUiModuleBuilder.Command<SourceRequest>(PatchId, "setSourceEnabled", TryReadSource,
                    (request, token) => backend.SetSourceEnabledAsync(request.Id, request.Enabled, token),
                    "The source payload is invalid."),
                SteamUiModuleBuilder.Command<FolderRequest>(PatchId, "addFolder", TryReadFolder,
                    (request, token) => backend.AddFolderAsync(request.Path, request.IncludeSubfolders, token),
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
               && SteamUiPayload.TryReadBoundedString(payload, "id", 64, out value);
    }

    private static bool TryReadSelected(JsonElement payload, out bool value)
    {
        value = false;
        if (!SteamUiPayload.HasExactly(payload, 1)
            || !payload.TryGetProperty("selected", out var selected)
            || selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = selected.GetBoolean();
        return true;
    }

    private static bool TryReadMode(JsonElement payload, out ModeRequest value)
    {
        value = new ModeRequest(string.Empty, string.Empty, false);
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var entryId)
            || !SteamUiPayload.TryReadBoundedString(payload, "mode", 32, out var modeValue)
            || !payload.TryGetProperty("acknowledged", out var acknowledged)
            || acknowledged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = new ModeRequest(entryId, modeValue, acknowledged.GetBoolean());
        return true;
    }

    private static bool TryReadSource(JsonElement payload, out SourceRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 32, out var id)
            || !TryReadFlag(payload, "enabled", out var enabled))
        {
            return false;
        }

        value = new SourceRequest(id, enabled);
        return true;
    }

    private static bool TryReadFolder(JsonElement payload, out FolderRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "path", 260, out var path)
            || !TryReadFlag(payload, "includeSubfolders", out var includeSubfolders))
        {
            return false;
        }

        value = new FolderRequest(path, includeSubfolders);
        return true;
    }

    private static bool TryReadRoute(JsonElement payload, out RouteRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !SteamUiPayload.TryReadBoundedString(payload, "route", 32, out var route))
        {
            return false;
        }

        value = new RouteRequest(id, route);
        return true;
    }

    private static bool TryReadCycle(JsonElement payload, out CycleRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !SteamUiPayload.TryReadBoundedString(payload, "asset", 8, out var asset)
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
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !SteamUiPayload.TryReadBoundedString(payload, "asset", 8, out var asset)
            || !SteamUiPayload.TryReadBoundedString(payload, "url", 2048, out var url))
        {
            return false;
        }

        value = new PickRequest(id, asset, url);
        return true;
    }

    private static bool TryReadAsset(JsonElement payload, out AssetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !SteamUiPayload.TryReadBoundedString(payload, "asset", 8, out var asset))
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
            || !SteamUiPayload.TryReadBoundedString(payload, "preference", 16, out var preference)
            || !TryReadFlag(payload, "onlyEmpty", out var onlyEmpty)
            || !TryReadOptionalString(payload, "asset", 8, out var asset))
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
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !TryReadOptionalString(payload, "query", 128, out var query))
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
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 64, out var id)
            || !TryReadOptionalString(payload, "provider", 32, out var provider)
            || !TryReadOptionalString(payload, "gameId", 64, out var gameId)
            || !TryReadOptionalString(payload, "name", 256, out var name))
        {
            return false;
        }

        value = new MatchRequest(id, provider, gameId, name);
        return true;
    }

    private static bool TryReadFlag(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(name, out var flag)
            || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = flag.GetBoolean();
        return true;
    }

    /// <summary>Reads a string that may be empty, which the bounded reader refuses.</summary>
    private static bool TryReadOptionalString(JsonElement payload, string name, int maximum, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = text.GetString() ?? string.Empty;
        return value.Length <= maximum;
    }

    /// <summary>One request to change an entry's launch mode.</summary>
    private readonly record struct ModeRequest(string Id, string Mode, bool Acknowledged);

    /// <summary>One request to tick or untick a source.</summary>
    private readonly record struct SourceRequest(string Id, bool Enabled);

    /// <summary>One request to add a shortcuts folder.</summary>
    private readonly record struct FolderRequest(string Path, bool IncludeSubfolders);

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
