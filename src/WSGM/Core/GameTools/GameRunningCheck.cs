using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Decides whether a game is closed enough for Game Tools to write its files.</summary>
/// <remarks>
///     A transaction fails closed: an unknown answer counts as running. The check covers Steam's running AppID,
///     the chosen program and the executables at the top of its folder and of the install root, each folder
///     listed afresh. The page's read-only check instead reuses the programs the folder walk already found and
///     only reports a game as running when that is known.
/// </remarks>
/// <param name="probe">The session's process answers.</param>
internal sealed class GameRunningCheck(IGameProcessProbe probe)
{
    // Bounds the executables taken from one folder, so a huge flat folder can't make the check slow.
    private const int FolderLimit = 64;

    private const string CloseFirst = "Close the game first.";

    /// <summary>Why a target's files can't be written now, or null when the game is known to be closed.</summary>
    /// <param name="target">The install target.</param>
    /// <returns>A user-facing reason, or null.</returns>
    internal string? ClosedMessage(GameToolTarget target)
    {
        if (probe.IsAppRunning(target.AppId) == true)
        {
            return CloseFirst;
        }

        return Running(target.InstallRoot, target.TargetDirectory, target.RuntimeExecutable) switch
        {
            true => CloseFirst,
            null => "WSGM couldn't confirm that the game is closed. Close it and try again.",
            _ => null
        };
    }

    /// <summary>The page's availability for a game whose folder was walked, from the programs the walk found.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="layout">The folder walk.</param>
    /// <param name="runtime">The chosen program's absolute path, or null.</param>
    /// <returns>
    ///     <c>running</c> with the reason to close the game only when it is known to be running, else <c>ready</c>
    ///     with no reason; a transaction still runs <see cref="ClosedMessage" /> itself.
    /// </returns>
    internal (string Availability, string? Reason) FolderAvailability(uint appId, GameFolderLayout layout,
        string? runtime)
    {
        return probe.IsAppRunning(appId) == true || Running(layout, runtime) == true
            ? ("running", "Close the game to change its tools.")
            : ("ready", null);
    }

    // Lists the programs in the target folder and the install root afresh, each folder once.
    private bool? Running(string root, string? directory, string? runtime)
    {
        List<string> paths = [];
        if (runtime is not null && File.Exists(runtime))
        {
            paths.Add(runtime);
        }

        string?[] folders = directory is not null && StoragePaths.Same(directory, root) ? [root] : [directory, root];
        foreach (var folder in folders.Where(folder => folder is not null && Directory.Exists(folder)))
        {
            try
            {
                paths.AddRange(Directory.EnumerateFiles(folder!, "*.exe", SearchOption.TopDirectoryOnly).Take(FolderLimit));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return Any(paths);
    }

    // Reads the programs the walk already found in the root and the runtime's folder.
    private bool? Running(GameFolderLayout layout, string? runtime)
    {
        if (runtime is not null && !StoragePaths.IsUnder(layout.Root, runtime))
        {
            return Running(layout.Root, Path.GetDirectoryName(runtime), runtime);
        }

        var directory = runtime is null ? "" : GameToolPaths.RelativeDirectory(layout.Root, Path.GetDirectoryName(runtime)!);
        List<string> paths = [];
        if (runtime is not null && File.Exists(runtime))
        {
            paths.Add(runtime);
        }

        paths.AddRange(layout.Executables.Where(file => file.Directory.Length == 0).Take(FolderLimit)
            .Select(file => file.FullPath));
        if (directory.Length > 0)
        {
            paths.AddRange(layout.Executables
                .Where(file => file.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)).Take(FolderLimit)
                .Select(file => file.FullPath));
        }

        return Any(paths);
    }

    private bool? Any(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var running = probe.ReadRunningPaths(paths);
        return running.Values.Any(value => value == true) ? true
            : running.Values.Any(value => value is null) ? null
            : false;
    }
}
