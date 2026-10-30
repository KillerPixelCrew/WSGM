using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The Game Tools state and commands the Overlay page uses; the Steam page drives the same owner.</summary>
/// <remarks>
///     Commands return as soon as work is admitted. Results, progress and rescans arrive through
///     <see cref="IChangeSource.Changed" /> and are read back with <see cref="Peek" />.
/// </remarks>
internal interface IGameToolsSource : IChangeSource
{
    /// <summary>The cached state of a game with the current progress, without scanning.</summary>
    /// <param name="appId">The game.</param>
    /// <returns>The state; a loading placeholder before the first scan.</returns>
    GameToolsState Peek(uint appId);

    /// <summary>Opens a game's page: scans it unless a scan just finished, and returns its state.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">Cancels waiting for the scan.</param>
    /// <returns>The fresh state, or the cached one while an operation runs.</returns>
    Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default);

    /// <summary>Rescans a game because the user asked.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Applied; the result follows through Changed.</returns>
    Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken);

    /// <summary>Looks up the latest release of every tool again.</summary>
    /// <param name="appId">The game whose page asked.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Applied; the result follows through Changed.</returns>
    Task<SteamUiCommandResult> CheckUpdatesAsync(uint appId, CancellationToken cancellationToken);

    /// <summary>Remembers the program the game runs.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="path">A candidate's relative path, or an absolute path the user picked.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the choice was accepted.</returns>
    Task<SteamUiCommandResult> ChooseRuntimeAsync(uint appId, string path, CancellationToken cancellationToken);

    /// <summary>Installs a tool, or replaces an installation made outside WSGM.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    Task<SteamUiCommandResult> InstallAsync(GameToolsInstallRequest request, CancellationToken cancellationToken);

    /// <summary>Updates an installed tool to the latest release.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="replaceConfig">Confirms taking the new stock configuration file when the format changes.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
        CancellationToken cancellationToken);

    /// <summary>Restores missing or changed files of the installed version.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken);

    /// <summary>Renames the installed proxy and changes settings.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="proxyName">The new proxy name, or null to keep it.</param>
    /// <param name="settings">Changed settings by id.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    Task<SteamUiCommandResult> ConfigureAsync(uint appId, string toolId, string? proxyName,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);

    /// <summary>Removes a tool and restores the game files it replaced.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken);

    /// <summary>Cancels a running download for a game.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>Applied when something was cancelled.</returns>
    Task<SteamUiCommandResult> CancelAsync(uint appId, CancellationToken cancellationToken);
}
