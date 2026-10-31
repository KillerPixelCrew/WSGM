using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record EmulatorPageState(
    EmulatorSnapshot Emulators,
    IReadOnlyList<RomSystemProfile> RomSystems,
    IReadOnlyList<RomSystemEmulatorChoices> Choices,
    IReadOnlyList<EmulatorDependencyCount> Dependencies,
    string Architecture)
{
    public IReadOnlyList<RomSystemProfile> RomSourceSystems => [RomProfiles.Automatic, .. RomSystems];
    public EmulatorBiosState Bios { get; init; } = new("", false, []);
    public EmulatorListItem[] Installed { get; init; } = [];
    public EmulatorCoreStatus[] CoreStatus { get; init; } = [];
    public string ShortcutStatus { get; init; } = "";
}

internal sealed record EmulatorListItem(string Id, string Detail, string Badge, bool UpdateAvailable);

internal sealed record EmulatorCoreStatus(string InstallationId, string CoreId, bool Missing);

internal sealed record EmulatorProgressState(bool Busy, string Status);

/// <summary>The emulator tool's commands, independent of library scans and Steam writes.</summary>
internal interface IEmulatorBackend : IChangeSource
{
    EmulatorPageState ReadPageState();
    EmulatorProgressState ReadProgressState();
    Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RefreshEmulatorsAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> SetBiosFolderAsync(string path, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> AddBiosFilesAsync(string path, string systemId, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> VerifyBiosAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RelinkBiosAsync(string systemId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> InstallEmulatorAsync(string definitionId, string channel,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> InstallEmulatorRuntimeDependenciesAsync(string definitionId,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> UseExternalEmulatorAsync(string definitionId, string executable,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> RemoveEmulatorAsync(string installationId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> SetEmulatorShortcutAsync(string installationId, bool enabled, int launchMode,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> IgnoreEmulatorVersionAsync(string installationId, string releaseId,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> OpenEmulatorReleaseNotesAsync(string definitionId, string channel, string architecture,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> ConfigureEmulatorPrerequisiteAsync(string installationId, string path, string kind,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> SetPreferredEmulatorAsync(string systemId, string installationId, string coreId,
        CancellationToken cancellationToken);
}
