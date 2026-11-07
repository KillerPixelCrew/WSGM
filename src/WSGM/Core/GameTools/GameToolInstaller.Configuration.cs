using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

internal sealed partial class GameToolInstaller
{
    private GameToolOutcome ConfigureNative(GameToolDefinition tool, GameToolTarget target, GameToolManifest current,
        IReadOnlyDictionary<string, string> settings, Func<string?> ensureClosed)
    {
        RequireWritable(target);
        var package = new GameToolPackage(GameToolReleases.FromManifest(current), target.TargetDirectory,
            current.Version, current.Family, current.Architecture);
        using var configuration = GameToolConfiguration.Prepare(tool, target.TargetDirectory, package, settings, current);
        var relative = tool.PrimaryConfig;
        var source = configuration.Files[relative];
        var path = Path.Combine(target.TargetDirectory, relative);
        var exists = File.Exists(path);
        var original = exists && current.Files.All(file => file.RelativePath != relative)
            && current.Replaced.All(file => file.RelativePath != relative);
        var transaction = GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id,
            original ? current.InstallId : "tx-" + Guid.NewGuid().ToString("N"));
        var backup = exists ? Path.Combine(transaction, relative) : "";
        var sha = Sha256(source);
        var previous = exists ? Sha256(path) : "";
        var item = new PlanItem(relative, source, exists ? GameToolJournalAction.Replace : GameToolJournalAction.Create,
            original, sha, previous, GameToolFileKind.Config);
        var journal = new GameToolJournalEntry { RelativePath = relative, Action = item.Action,
            BackupPath = backup, NewSha256 = sha, PreviousSha256 = previous };
        var pending = current with { State = GameToolManifestState.Pending, Journal = [journal],
            JournalDirectories = [], Previous = current with { Previous = null }, Operation = "configure" };
        Persist(target.AppId, pending);
        try
        {
            RequireClosed(ensureClosed);
            var unchanged = current.Files.Where(file => file.RelativePath != relative)
                .ToDictionary(file => file.RelativePath, file => (string?)file.Sha256, StringComparer.OrdinalIgnoreCase);
            var plan = new InstallPlan([item], [], unchanged);
            Execute(target.TargetDirectory, plan, [journal]);
            var result = current with { Files = BuildOwned(tool, target.TargetDirectory, plan, current),
                Settings = configuration.Values, UpdatedUtc = DateTime.UtcNow,
                Replaced = original ? [.. current.Replaced, new GameToolReplacedFile
                { RelativePath = relative, BackupPath = backup, OriginalSha256 = previous }] : current.Replaced };
            Persist(target.AppId, result);
            if (!original)
            {
                GameToolStorage.DeleteTree(transaction);
            }

            return new GameToolOutcome(result, "Settings saved to " + relative + ". They apply on the next game start.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Translate(ex, Rollback(target.AppId, target.TargetDirectory, pending));
        }
    }
}
