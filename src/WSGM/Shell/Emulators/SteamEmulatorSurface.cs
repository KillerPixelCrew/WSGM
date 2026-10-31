using System;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The independently published emulator tool in Steam.</summary>
internal static class SteamEmulatorSurface
{
    internal const string PatchId = "wsgm.emulators";
    internal const string ProgressId = "wsgm.emulators.progress";
    internal const string Route = "/wsgm/emulators";
    internal const string Template = "emulators";
    internal const string GateName = "emulatorManager";

    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "emulator-manager-v2:steam-page", "Emulator manager",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Modal,
            SteamPageProbe.ShowModal
        ]);

    internal static ISteamUiModule Module(Func<bool> enabled, EmulatorService backend)
    {
        return new SteamUiModule("emulators", [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled,
                    () => new ValueTask<EmulatorPageState?>(backend.ReadPageState()),
                    GameLibraryJsonContext.Default.EmulatorPageState, () => backend.CatalogRevision),
                SteamUiModuleBuilder.Publication(ProgressId, enabled,
                    () => new ValueTask<EmulatorProgressState?>(backend.ReadProgressState()),
                    GameLibraryJsonContext.Default.EmulatorProgressState, () => backend.ProgressRevision)
            ],
            [
                SteamUiModuleBuilder.Command(PatchId, "cancel", backend.CancelAsync),
                SteamUiModuleBuilder.Command(PatchId, "refreshEmulators", backend.RefreshEmulatorsAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "installEmulatorRuntimes", TryReadDefinition,
                    backend.InstallEmulatorRuntimeDependenciesAsync, "Choose an emulator definition."),
                SteamUiModuleBuilder.Command(PatchId, "verifyBios", backend.VerifyBiosAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "setBiosFolder", TryReadPath,
                    backend.SetBiosFolderAsync, "Choose an existing BIOS folder."),
                SteamUiModuleBuilder.Command<BiosRequest>(PatchId, "addBiosFiles", TryReadBios,
                    (value, token) => backend.AddBiosFilesAsync(value.Path, value.SystemId, token),
                    "The BIOS file selection is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "relinkBios", TryReadSystem,
                    backend.RelinkBiosAsync, "The BIOS system is invalid."),
                SteamUiModuleBuilder.Command<ReleaseRequest>(PatchId, "installEmulator", TryReadRelease,
                    (value, token) => backend.InstallEmulatorAsync(value.DefinitionId, value.Channel, token),
                    "The emulator release is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "updateEmulator", TryReadInstallation,
                    backend.UpdateEmulatorAsync, "The emulator installation is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "repairEmulator", TryReadInstallation,
                    backend.RepairEmulatorAsync, "The emulator installation is invalid."),
                SteamUiModuleBuilder.Command<ExternalRequest>(PatchId, "useExternalEmulator", TryReadExternal,
                    (value, token) => backend.UseExternalEmulatorAsync(value.DefinitionId, value.Path, token),
                    "The external emulator is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "removeEmulator", TryReadInstallation,
                    backend.RemoveEmulatorAsync,
                    "The emulator removal is invalid."),
                SteamUiModuleBuilder.Command<ShortcutRequest>(PatchId, "setEmulatorShortcut", TryReadShortcut,
                    (value, token) => backend.SetEmulatorShortcutAsync(value.InstallationId, value.Enabled,
                        value.LaunchMode, token), "The emulator shortcut settings are invalid."),
                SteamUiModuleBuilder.Command<IgnoreRequest>(PatchId, "ignoreEmulatorVersion", TryReadIgnore,
                    (value, token) => backend.IgnoreEmulatorVersionAsync(value.InstallationId, value.ReleaseId, token),
                    "The release choice is invalid."),
                SteamUiModuleBuilder.Command<NotesRequest>(PatchId, "openEmulatorReleaseNotes", TryReadNotes,
                    (value, token) => backend.OpenEmulatorReleaseNotesAsync(value.DefinitionId, value.Channel,
                        value.Architecture, token),
                    "The emulator release is invalid."),
                SteamUiModuleBuilder.Command<PrerequisiteRequest>(PatchId, "configureEmulatorPrerequisite",
                    TryReadPrerequisite,
                    (value, token) =>
                        backend.ConfigureEmulatorPrerequisiteAsync(value.InstallationId, value.Path, value.Kind, token),
                    "The emulator setup is invalid."),
                SteamUiModuleBuilder.Command<PreferredRequest>(PatchId, "setPreferredEmulator", TryReadPreferred,
                    (value, token) =>
                        backend.SetPreferredEmulatorAsync(value.SystemId, value.InstallationId, value.CoreId, token),
                    "The preferred emulator is invalid.")
            ]);
    }

    private static bool TryReadInstallation(JsonElement payload, out string id)
    {
        id = "";
        return SteamUiPayload.HasExactly(payload, 1) &&
               SteamUiPayload.TryReadNonBlankString(payload, "installationId", out id);
    }

    private static bool TryReadShortcut(JsonElement payload, out ShortcutRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "installationId", out var id)
            || !payload.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !payload.TryGetProperty("launchMode", out var mode) || mode.ValueKind != JsonValueKind.Number
            || !mode.TryGetInt32(out var launchMode)
            || launchMode is < 0 or > 3)
        {
            return false;
        }

        value = new ShortcutRequest(id, enabled.GetBoolean(), launchMode);
        return true;
    }

    private static bool TryReadPath(JsonElement payload, out string path)
    {
        path = "";
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadNonBlankString(payload, "path", out path);
    }

    private static bool TryReadDefinition(JsonElement payload, out string definitionId)
    {
        definitionId = "";
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadNonBlankString(payload, "definitionId", out definitionId);
    }

    private static bool TryReadSystem(JsonElement payload, out string systemId)
    {
        systemId = "";
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadString(payload, "systemId", out systemId);
    }

    private static bool TryReadBios(JsonElement payload, out BiosRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.TryReadString(payload, "systemId", out var systemId))
        {
            return false;
        }

        value = new BiosRequest(path, systemId);
        return true;
    }

    private static bool TryReadRelease(JsonElement payload, out ReleaseRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "definitionId", out var definition)
            || !SteamUiPayload.TryReadNonBlankString(payload, "channel", out var channel))
        {
            return false;
        }

        value = new ReleaseRequest(definition, channel);
        return true;
    }

    private static bool TryReadExternal(JsonElement payload, out ExternalRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "definitionId", out var definition)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path))
        {
            return false;
        }

        value = new ExternalRequest(definition, path);
        return true;
    }

    private static bool TryReadNotes(JsonElement payload, out NotesRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "definitionId", out var definition)
            || !SteamUiPayload.TryReadNonBlankString(payload, "channel", out var channel)
            || !SteamUiPayload.TryReadNonBlankString(payload, "architecture", out var architecture))
        {
            return false;
        }

        value = new NotesRequest(definition, channel, architecture);
        return true;
    }

    private static bool TryReadIgnore(JsonElement payload, out IgnoreRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "installationId", out var installation)
            || !SteamUiPayload.TryReadNonBlankString(payload, "releaseId", out var release))
        {
            return false;
        }

        value = new IgnoreRequest(installation, release);
        return true;
    }

    private static bool TryReadPrerequisite(JsonElement payload, out PrerequisiteRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "installationId", out var installation)
            || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.TryReadNonBlankString(payload, "kind", out var kind))
        {
            return false;
        }

        value = new PrerequisiteRequest(installation, path, kind);
        return true;
    }

    private static bool TryReadPreferred(JsonElement payload, out PreferredRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "systemId", out var system)
            || !SteamUiPayload.TryReadString(payload, "installationId", out var installation)
            || !SteamUiPayload.TryReadString(payload, "coreId", out var core))
        {
            return false;
        }

        value = new PreferredRequest(system, installation, core);
        return true;
    }

    private readonly record struct BiosRequest(string Path, string SystemId);
    private readonly record struct ShortcutRequest(string InstallationId, bool Enabled, int LaunchMode);

    private readonly record struct ReleaseRequest(string DefinitionId, string Channel);

    private readonly record struct NotesRequest(string DefinitionId, string Channel, string Architecture);

    private readonly record struct ExternalRequest(string DefinitionId, string Path);

    private readonly record struct IgnoreRequest(string InstallationId, string ReleaseId);

    private readonly record struct PrerequisiteRequest(string InstallationId, string Path, string Kind);

    private readonly record struct PreferredRequest(string SystemId, string InstallationId, string CoreId);
}
