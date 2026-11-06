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
        List<(GameToolOwnedFile File, string Destination)> moves = [];
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
            var original = current.Replaced.FirstOrDefault(item => item.RelativePath == active);
            if (File.Exists(destinationPath)
                && (!enabled || original is null || Sha256(destinationPath) != original.OriginalSha256))
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
                Action = File.Exists(destinationPath) ? GameToolJournalAction.Replace : GameToolJournalAction.Create,
                BackupPath = File.Exists(destinationPath) ? Path.Combine(transaction, destination) : "",
                PreviousSha256 = File.Exists(destinationPath) ? Sha256(destinationPath) : "",
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
            moves.Add((file, destination));
        }

        var pending = current with
        {
            State = GameToolManifestState.Pending, Journal = journal, Previous = current with { Previous = null },
            JournalDirectories = [], Operation = "configure"
        };
        Persist(target.AppId, pending);
        try
        {
            RequireClosed(ensureClosed);
            Directory.CreateDirectory(transaction);
            // Finish all recovery copies before changing any game files.
            foreach (var entry in journal.Where(entry => entry.BackupPath.Length > 0))
            {
                var path = Path.Combine(directory, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath)!);
                File.Copy(path, entry.BackupPath);
            }

            List<GameToolOwnedFile> files = [.. current.Files.Where(file => file.Kind != GameToolFileKind.Binary)];
            foreach (var (file, destination) in moves)
            {
                var source = Path.Combine(directory, file.RelativePath);
                var destinationPath = Path.Combine(directory, destination);
                CopyInto(source, destinationPath);
                ClearReadOnly(source);
                File.Delete(source);
                if (!enabled && current.Replaced.FirstOrDefault(item => item.RelativePath == file.RelativePath) is { } original)
                {
                    CopyInto(original.BackupPath, source);
                }

                var info = new FileInfo(destinationPath);
                files.Add(file with { RelativePath = destination, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            }

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
