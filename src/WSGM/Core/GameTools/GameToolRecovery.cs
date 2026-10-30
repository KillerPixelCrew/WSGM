using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core.GameTools;

/// <summary>What recovery did to a game's record.</summary>
/// <param name="Record">The record after recovery and rebasing, or null when the game has none.</param>
/// <param name="Notice">A user-facing summary of what was undone, or null when nothing was.</param>
/// <remarks>
///     A change that still waits, because the game may be running or its folder couldn't be reached, and one whose
///     rollback failed stay visible through their manifests' states in <paramref name="Record" />.
/// </remarks>
internal sealed record GameToolRecoveryResult(GameToolRecord? Record, string? Notice);

/// <summary>Reconciles a game's durable record with the folder Steam reports now.</summary>
/// <remarks>
///     <para>
///         A <see cref="GameToolManifestState.Pending" /> manifest is a transaction a crash interrupted. It is rolled
///         back from its journal, but only once the game is known to be closed, because rolling back writes game
///         files; until then it stays pending and the page shows it as interrupted. A rollback that can't finish
///         leaves the manifest <see cref="GameToolManifestState.Failed" />, which is never retried automatically.
///     </para>
///     <para>
///         Manifest paths are relative to the install root, so a game that moved to another library is rebased by
///         recording the new root.
///     </para>
/// </remarks>
/// <param name="userRoot">The per-user data root.</param>
/// <param name="installer">The transaction owner that rolls journals back.</param>
/// <param name="running">Decides whether the game is closed.</param>
internal sealed class GameToolRecovery(string userRoot, GameToolInstaller installer, GameRunningCheck running)
{
    /// <summary>Rolls back interrupted transactions and rebases a moved game.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="root">The install root Steam reports now; the folder exists.</param>
    /// <param name="record">The game's record as last read, or null.</param>
    /// <param name="catalog">The catalog that names the tools in the notice.</param>
    /// <returns>The outcome, with the record to use from now on.</returns>
    internal GameToolRecoveryResult Recover(uint appId, string root, GameToolRecord? record, GameToolCatalog catalog)
    {
        if (record is null)
        {
            return new GameToolRecoveryResult(null, null);
        }

        List<string> undone = [];
        var attempted = false;
        foreach (var manifest in record.Tools.Where(manifest => manifest.State == GameToolManifestState.Pending))
        {
            var target = GameToolTarget.FromManifest(appId, root, manifest);
            if (running.ClosedMessage(target) is not null)
            {
                Log.Info($"Game Tools: interrupted {manifest.Operation} of {manifest.ToolId} for {appId} waits "
                         + "until the game is closed.");
                continue;
            }

            Log.Warn($"Game Tools: rolling back an interrupted {manifest.Operation} of {manifest.ToolId} for {appId}.");
            attempted = true;
            if (installer.Rollback(appId, GameToolPaths.Resolve(root, manifest.TargetDirectory), manifest))
            {
                undone.Add(manifest.ToolId);
            }
        }

        if (attempted)
        {
            // A rollback that couldn't reach the folder leaves the manifest pending for the next scan; one that left
            // files it couldn't restore marks it failed for the user's Remove. The record read now shows both.
            record = GameToolStorage.ReadLocked(userRoot).Find(appId);
        }

        if (record?.Tools.Any(manifest => !StoragePaths.Same(manifest.InstallRoot, root)) == true)
        {
            Log.Info($"Game Tools: game {appId} moved to {root}; rebasing its manifests.");
            record = GameToolStorage.Rebase(userRoot, appId, root);
        }

        return new GameToolRecoveryResult(record, Notice(undone, catalog));
    }

    private static string? Notice(IReadOnlyList<string> undone, GameToolCatalog catalog)
    {
        if (undone.Count == 0)
        {
            return null;
        }

        var names = undone.Select(id => catalog.Find(id)?.Name ?? id);
        return "An interrupted change to " + string.Join(" and ", names) + " was undone.";
    }
}
