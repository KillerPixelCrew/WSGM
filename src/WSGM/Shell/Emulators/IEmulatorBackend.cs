using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The emulator tool's commands, independent of library scans and Steam writes.</summary>
internal interface IEmulatorBackend : IChangeSource
{
    /// <summary>
    ///     Advances whenever <see cref="ReadPageState" />'s projection changes: catalogue, installations, BIOS,
    ///     choices, dependencies or shortcut status. Progress alone never advances it.
    /// </summary>
    long CatalogRevision { get; }

    /// <summary>The page projection; its catalogue carries no busy state or status line.</summary>
    EmulatorPageState ReadPageState();

    /// <summary>The manager's busy state and status line, published apart from the catalogue.</summary>
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
