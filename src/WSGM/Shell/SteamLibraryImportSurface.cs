using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>One title in the import preview.</summary>
/// <param name="Id">Opaque identity for this publication, not the title's own.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Source">Which source found it.</param>
/// <param name="Identity">Its identity in that source, shown for diagnosis.</param>
/// <param name="InstallPath">Where it is installed.</param>
/// <param name="LaunchLabel">What its launch route is called.</param>
/// <param name="LaunchValidated">Whether that route is validated.</param>
/// <param name="LaunchEvidence">Why, in one sentence.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence.</param>
/// <param name="Mode">Which route it would launch with.</param>
/// <param name="CanUseSteamIntegration">Whether the overlay route is available for it at all.</param>
/// <param name="RequiresAcknowledgement">Whether choosing that route needs the risk accepted.</param>
/// <param name="Acknowledged">Whether the user has accepted it.</param>
/// <param name="Action">What a sync would do.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Selected">Whether the user has it selected.</param>
/// <param name="Selectable">Whether it may be selected at all.</param>
/// <param name="Excluded">Whether the user said not to import it.</param>
/// <param name="Notes">Anything else worth showing.</param>
public sealed record SteamLibraryImportEntry(
    string Id,
    string Name,
    string Source,
    string Identity,
    string InstallPath,
    string LaunchLabel,
    bool LaunchValidated,
    string LaunchEvidence,
    string Multiplayer,
    string MultiplayerEvidence,
    string Mode,
    bool CanUseSteamIntegration,
    bool RequiresAcknowledgement,
    bool Acknowledged,
    string Action,
    string Reason,
    bool Selected,
    bool Selectable,
    bool Excluded,
    IReadOnlyList<string> Notes);

/// <summary>Everything the import page renders.</summary>
/// <param name="SourceName">The source being imported from.</param>
/// <param name="Phase">idle, scanning, review, applying or done.</param>
/// <param name="Entries">What the scan found.</param>
/// <param name="SelectedCount">How many are selected.</param>
/// <param name="AddCount">How many would be created.</param>
/// <param name="UpdateCount">How many would be rewritten.</param>
/// <param name="RemoveCount">How many would be deleted.</param>
/// <param name="SkipCount">How many need nothing.</param>
/// <param name="ConflictCount">How many were changed by hand.</param>
/// <param name="UnroutableCount">How many have no validated launch route.</param>
/// <param name="Progress">How many entries of an apply are done.</param>
/// <param name="ProgressTotal">How many an apply will do.</param>
/// <param name="LauncherAvailable">Whether the packaged-game launcher is installed.</param>
/// <param name="LauncherDetail">Why it is not, when it is not.</param>
/// <param name="Loading">Whether work is in flight.</param>
/// <param name="Notice">Something worth saying that is not an error.</param>
/// <param name="Error">Why the last operation did not do what was asked.</param>
/// <param name="Revision">Monotonic publication revision.</param>
public sealed record SteamLibraryImportState(
    string SourceName,
    string Phase,
    IReadOnlyList<SteamLibraryImportEntry> Entries,
    int SelectedCount,
    int AddCount,
    int UpdateCount,
    int RemoveCount,
    int SkipCount,
    int ConflictCount,
    int UnroutableCount,
    int Progress,
    int ProgressTotal,
    bool LauncherAvailable,
    string? LauncherDetail = null,
    bool Loading = false,
    string? Notice = null,
    string? Error = null,
    long Revision = 0);

/// <summary>Answers the import page's explicit operations.</summary>
public interface ISteamLibraryImportBackend
{
    /// <summary>Scans the source. Writes nothing.</summary>
    Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Cancels a scan or an apply in progress.</summary>
    Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken);

    /// <summary>Selects or deselects one entry.</summary>
    Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken);

    /// <summary>Selects or deselects everything that may be selected.</summary>
    Task<SteamUiCommandResult> SelectAllAsync(bool selected, CancellationToken cancellationToken);

    /// <summary>Changes one entry's launch mode.</summary>
    /// <remarks>
    ///     The acknowledgement is checked here, in the host, not only in the page: a page defect
    ///     must not be able to put a multiplayer title on the injection route.
    /// </remarks>
    Task<SteamUiCommandResult> SetModeAsync(
        string id, string mode, bool acknowledged, CancellationToken cancellationToken);

    /// <summary>Leaves a title out of this and every later scan until the user offers it again.</summary>
    Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Offers a left-out title again.</summary>
    Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies the selected entries.</summary>
    Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken);
}

/// <summary>The library importer's page inside Steam.</summary>
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
        Func<ValueTask<SteamLibraryImportState?>> read,
        ISteamLibraryImportBackend backend,
        string id = "library-import")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, LibraryImportJsonContext.Default.SteamLibraryImportState)
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamLibraryImportState))]
internal sealed partial class LibraryImportJsonContext : JsonSerializerContext;
