using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

internal sealed partial class GameToolInstaller
{
    /// <summary>Parks or restores every owned binary as one journaled per-game profile change.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="target">The revalidated game target.</param>
    /// <param name="current">The installed manifest.</param>
    /// <param name="enabled">Whether its injection files should be active.</param>
    /// <param name="ensureClosed">Checks the game's running state immediately before the first write.</param>
    /// <returns>The saved state notice.</returns>
    internal string SetEnabled(GameToolDefinition tool, GameToolTarget target, GameToolManifest current,
        bool enabled, Func<string?> ensureClosed)
    {
        GameToolAdmission.RequireCompleted(current);
        if (current.Enabled == enabled)
        {
            return tool.Name + (enabled ? " is enabled." : " is disabled.");
        }

        RequireWritable(target);
        var suffix = ".wsgm-disabled-" + tool.Id;
        var directory = target.TargetDirectory;
        var transaction = GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id,
            "tx-" + Guid.NewGuid().ToString("N"));
        List<GameToolJournalEntry> journal = [];
        List<PlanItem> items = [];
        var originals = current.Replaced.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in current.Files.Where(file => file.Kind == GameToolFileKind.Binary))
        {
            var source = Path.Combine(directory, file.RelativePath);
            if (!File.Exists(source) || Sha256(source) != file.Sha256)
            {
                throw new GameToolException(file.RelativePath + " is missing or changed. No enabled state was changed.");
            }

            if (enabled && !file.RelativePath.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new GameToolException("The disabled file record is invalid.");
            }

            var active = enabled ? file.RelativePath[..^suffix.Length] : file.RelativePath;
            var parked = active + suffix;
            var destination = enabled ? active : parked;
            var destinationPath = Path.Combine(directory, destination);
            var original = originals.GetValueOrDefault(active);
            var destinationExists = File.Exists(destinationPath);
            var destinationSha = destinationExists ? Sha256(destinationPath) : "";
            if (destinationExists
                && (!enabled || original is null || destinationSha != original.OriginalSha256))
            {
                throw new GameToolException(destination + " already exists or changed. It was left untouched.");
            }

            if (Directory.Exists(destinationPath))
            {
                throw new GameToolException(destination + " is a directory and cannot be replaced.");
            }

            if (original is not null && (!File.Exists(original.BackupPath)
                                         || Sha256(original.BackupPath) != original.OriginalSha256))
            {
                throw new GameToolException("The original backup of " + active + " is missing or changed.");
            }

            journal.Add(new GameToolJournalEntry
            {
                RelativePath = destination,
                Action = destinationExists ? GameToolJournalAction.Replace : GameToolJournalAction.Create,
                BackupPath = destinationExists ? Path.Combine(transaction, destination) : "",
                PreviousSha256 = destinationSha,
                NewSha256 = file.Sha256
            });
            journal.Add(new GameToolJournalEntry
            {
                RelativePath = file.RelativePath,
                Action = !enabled && original is not null ? GameToolJournalAction.Replace : GameToolJournalAction.Delete,
                BackupPath = Path.Combine(transaction, file.RelativePath),
                PreviousSha256 = file.Sha256,
                NewSha256 = !enabled && original is not null ? original.OriginalSha256 : ""
            });
            items.Add(new PlanItem(destination, source,
                destinationExists ? GameToolJournalAction.Replace : GameToolJournalAction.Create,
                false, file.Sha256, destinationSha, GameToolFileKind.Binary));
            items.Add(new PlanItem(file.RelativePath, !enabled ? original?.BackupPath : null,
                !enabled && original is not null ? GameToolJournalAction.Replace : GameToolJournalAction.Delete,
                false, !enabled && original is not null ? original.OriginalSha256 : "", file.Sha256,
                GameToolFileKind.Binary, Restore: !enabled && original is not null));
        }

        var pending = current with
        {
            State = GameToolManifestState.Pending, Journal = journal, Previous = current with { Previous = null },
            JournalDirectories = [], Operation = enabled ? "enable" : "disable"
        };
        Persist(target.AppId, pending);
        try
        {
            RequireClosed(ensureClosed);
            var unchanged = current.Files.Where(file => file.Kind != GameToolFileKind.Binary)
                .ToDictionary(file => file.RelativePath, file => (string?)file.Sha256, StringComparer.OrdinalIgnoreCase);
            var plan = new InstallPlan(items, [], unchanged);
            Execute(directory, plan, journal);
            var files = BuildOwned(tool, directory, plan, current);
            Persist(target.AppId, current with { Enabled = enabled, Files = files, UpdatedUtc = DateTime.UtcNow });
            GameToolStorage.DeleteTree(transaction);
            return tool.Name + (enabled ? " enabled for this game." : " disabled for this game. Files and settings are kept.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var undone = Rollback(target.AppId, directory, pending);
            throw Translate(ex, undone);
        }
    }
}
