using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>The rules a Game Tools request must pass before a transaction may start.</summary>
/// <remarks>
///     Validation of the request itself returns the refusal so a command can be refused before work is admitted.
///     The checks that need a fresh scan run inside the operation and throw <see cref="GameToolException" />,
///     like the installer.
/// </remarks>
internal static class GameToolAdmission
{
    /// <summary>Checks that every setting id and value is one the tool offers.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="settings">Setting values by id.</param>
    /// <returns>A user-facing refusal, or null.</returns>
    internal static string? Settings(GameToolDefinition tool, IReadOnlyDictionary<string, string> settings)
    {
        foreach (var (id, value) in settings)
        {
            var setting = tool.Settings.FirstOrDefault(item => item.Id == id);
            if (setting is null || setting.Options.All(option => option.Value != value))
            {
                return "Unknown setting or value: " + id + ".";
            }
        }

        return null;
    }

    /// <summary>Checks a configure request: known settings, and something to change.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="proxyName">The new proxy name, or null to keep it.</param>
    /// <param name="settings">Changed settings by id.</param>
    /// <returns>A user-facing refusal, or null.</returns>
    internal static string? Configure(GameToolDefinition tool, string? proxyName,
        IReadOnlyDictionary<string, string> settings)
    {
        return Settings(tool, settings) ?? (proxyName is null && settings.Count == 0 ? "Nothing to change." : null);
    }

    /// <summary>Checks that a game has a folder to work in.</summary>
    /// <param name="hasFolder">Whether the scan found the game's folder.</param>
    /// <param name="reason">Why it has none, or null.</param>
    /// <returns>A user-facing refusal, or null when the folder is there.</returns>
    internal static string? Folder(bool hasFolder, string? reason)
    {
        return hasFolder ? null : reason ?? "The game's folder is not available.";
    }

    /// <summary>Requires that a fresh install may start and returns where it goes: beside the chosen program.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="appId">The game.</param>
    /// <param name="installRoot">The game's current install root.</param>
    /// <param name="availability">The game's availability from the scan.</param>
    /// <param name="reason">Why the game is not ready, or null.</param>
    /// <param name="record">The game's record after recovery, or null.</param>
    /// <param name="resolution">The folder's program resolution, or null when the folder wasn't read.</param>
    /// <param name="antiCheat">Anti-cheat systems recognised in the folder.</param>
    /// <param name="acknowledgeRisk">Whether the user accepted the anti-cheat warning.</param>
    /// <returns>The install target.</returns>
    /// <exception cref="GameToolException">The install is refused; the message says why.</exception>
    internal static GameToolTarget RequireInstallable(GameToolDefinition tool, uint appId, string installRoot,
        string availability, string? reason, GameToolRecord? record, GameRuntimeResolution? resolution,
        IReadOnlyList<string> antiCheat, bool acknowledgeRisk)
    {
        if (availability != "ready")
        {
            throw new GameToolException(reason ?? "The game is not available.");
        }

        if (record?.Manifest(tool.Id) is not null)
        {
            throw new GameToolException(tool.Name + " is already installed. Use Update or Repair.");
        }

        if (resolution?.Chosen is not { } runtime)
        {
            throw new GameToolException("Choose the game's program first.");
        }

        if (antiCheat.Count > 0 && !acknowledgeRisk)
        {
            throw new GameToolException(tool.AntiCheatWarning + " Confirm the warning to continue.");
        }

        if (GameToolInstaller.Incompatible(tool, runtime.Architecture) is { } incompatible)
        {
            throw new GameToolException(incompatible);
        }

        return GameToolTarget.FromRuntime(appId, installRoot, resolution, runtime);
    }

    /// <summary>Requires the manifest WSGM installed, for configure, remove, update and repair.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="hasFolder">Whether the game has a folder to work in.</param>
    /// <param name="reason">Why it has none, or null.</param>
    /// <param name="record">The game's record after recovery, or null.</param>
    /// <returns>The manifest, in any state.</returns>
    /// <exception cref="GameToolException">There is nothing WSGM installed to act on.</exception>
    internal static GameToolManifest RequireInstalled(GameToolDefinition tool, bool hasFolder, string? reason,
        GameToolRecord? record)
    {
        if (Folder(hasFolder, reason) is { } refused)
        {
            throw new GameToolException(refused);
        }

        return record?.Manifest(tool.Id)
               ?? throw new GameToolException(tool.Name + " is not installed by WSGM for this game.");
    }

    /// <summary>Requires a completed installation for update and repair.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <exception cref="GameToolException">An interrupted change waits for the user's Remove.</exception>
    /// <remarks>Only Remove may finish an interrupted change, because it is the one that forgets changed files.</remarks>
    internal static void RequireCompleted(GameToolManifest manifest)
    {
        if (manifest.State != GameToolManifestState.Installed)
        {
            throw new GameToolException("An earlier change was interrupted. Use Remove to finish undoing it.");
        }
    }

    /// <summary>Checks the program a user chose for a game and describes it for the record.</summary>
    /// <param name="installRoot">The game's current install root.</param>
    /// <param name="path">A candidate's relative path, or an absolute path the user picked.</param>
    /// <param name="record">The game's record, or null.</param>
    /// <param name="catalog">The catalog naming installed tools.</param>
    /// <param name="choice">Receives the program to remember when accepted.</param>
    /// <returns>A user-facing refusal, or null when the choice is accepted.</returns>
    /// <remarks>
    ///     A program inside the install root is accepted wherever it sits; one outside it also passes the broad-folder
    ///     guard. Tools are installed beside the chosen program, so the choice can't move away from an installed tool.
    /// </remarks>
    internal static string? RuntimeChoice(string installRoot, string path, GameToolRecord? record,
        GameToolCatalog catalog, out GameRuntimeChoice? choice)
    {
        choice = null;
        string full;
        try
        {
            full = Path.GetFullPath(GameToolPaths.Resolve(installRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Choose the game's program.";
        }

        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return "Choose the game's program (.exe).";
        }

        if (GameFolderGuard.Reject(full, StoragePaths.IsUnder(installRoot, full)) is { } refused)
        {
            return refused;
        }

        if (PeImage.TryArchitecture(full) is not { Length: > 0 })
        {
            return "That file is not a Windows program WSGM can read.";
        }

        var installedElsewhere = record?.Tools.FirstOrDefault(manifest =>
            !StoragePaths.Same(GameToolPaths.Resolve(installRoot, manifest.TargetDirectory),
                Path.GetDirectoryName(full)!));
        if (installedElsewhere is not null)
        {
            var name = catalog.Find(installedElsewhere.ToolId)?.Name ?? installedElsewhere.ToolId;
            return "Remove " + name + " first; it is installed beside the current program.";
        }

        var info = new FileInfo(full);
        choice = new GameRuntimeChoice
        {
            RelativePath = GameToolPaths.Relative(installRoot, full),
            Size = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc
        };
        return null;
    }
}
