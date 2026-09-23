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
        ["scan", "cancel", "toggleEntry", "selectAll", "setMode", "exclude", "include", "apply"];

    /// <summary>Installs the import renderer and its state subscription.</summary>
    /// <remarks>
    ///     The same native components the artwork page resolves, minus the ones this does not draw:
    ///     requiring a component that is never rendered would make the gate refuse over something
    ///     that does not matter.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "libraryImport",
        "steam-library-import-v1:native-steam-components",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_library_import_probe_")}}
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}}),
              controls:count({{SteamUiProbeJs.NativeFieldTokens}}),
              modal:count({{SteamUiProbeJs.NativeModalTokens}}),
              showModal:count({{SteamUiProbeJs.NativeShowModalTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.IsOne(root, "react")
                && SteamUiPatchEvaluation.IsOne(root, "focusable")
                && SteamUiPatchEvaluation.IsOne(root, "controls")
                && SteamUiPatchEvaluation.IsOne(root, "modal")
                && SteamUiPatchEvaluation.IsOne(root, "showModal"),
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
                SteamUiModuleBuilder.Command(PatchId, "apply", backend.ApplyAsync)
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

    /// <summary>One request to change an entry's launch mode.</summary>
    private readonly record struct ModeRequest(string Id, string Mode, bool Acknowledged);
}
