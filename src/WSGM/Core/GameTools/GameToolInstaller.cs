using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core.GameTools;

/// <summary>Where a tool is installed for one game.</summary>
/// <param name="AppId">The game.</param>
/// <param name="InstallRoot">The game's current install root.</param>
/// <param name="RuntimeExecutable">The absolute runtime executable; its folder is the target directory.</param>
/// <param name="Architecture">The runtime executable's architecture.</param>
/// <param name="Engine">The detected engine id.</param>
internal sealed record GameToolTarget(uint AppId, string InstallRoot, string RuntimeExecutable, string Architecture,
    string Engine)
{
    /// <summary>The absolute directory the tool is installed into.</summary>
    internal string TargetDirectory => Path.GetDirectoryName(RuntimeExecutable)!;

    /// <summary>The target directory relative to the install root, or absolute outside it.</summary>
    internal string RelativeTargetDirectory => GameToolPaths.RelativeDirectory(InstallRoot, TargetDirectory);
}

/// <summary>An unmanaged copy of the tool found in the target directory.</summary>
/// <param name="RelativePath">The file relative to the target directory.</param>
/// <param name="Version">Its product version.</param>
internal sealed record GameToolUnmanagedCopy(string RelativePath, string Version);

/// <summary>Availability of one proxy name in the target directory.</summary>
/// <param name="Name">The proxy file name.</param>
/// <param name="Hint">When to choose it.</param>
/// <param name="Available">Whether installing under this name is possible now.</param>
/// <param name="Conflict">Why not, or null.</param>
/// <param name="Suggest">Whether WSGM may pick it on its own.</param>
internal sealed record GameToolProxyStatus(string Name, string Hint, bool Available, string? Conflict, bool Suggest);

/// <summary>A read-only inspection of a target directory for one tool.</summary>
/// <param name="Unmanaged">Identity-matched copies WSGM does not own.</param>
/// <param name="Proxies">Every offered proxy name and whether it can be used.</param>
/// <param name="Missing">Owned files that are gone.</param>
/// <param name="Modified">Owned binaries whose size or time changed.</param>
internal sealed record GameToolInspection(
    IReadOnlyList<GameToolUnmanagedCopy> Unmanaged,
    IReadOnlyList<GameToolProxyStatus> Proxies,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Modified);

/// <summary>What an install, update or repair should do.</summary>
/// <param name="Operation">
///     <c>install</c>, <c>replaceExisting</c> (back up and replace an unmanaged installation), <c>update</c> or
///     <c>repair</c>.
/// </param>
/// <param name="ProxyName">The name the primary file is installed under.</param>
/// <param name="Settings">Setting values by id; missing ids keep their current or default value.</param>
/// <param name="ReplaceConfig">Take the package's stock configuration file, then re-apply WSGM's settings.</param>
internal sealed record GameToolRequest(
    string Operation,
    string ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool ReplaceConfig = false)
{
    /// <summary>Whether an unmanaged installation is backed up and replaced instead of refusing the install.</summary>
    internal bool ReplaceExisting => Operation == "replaceExisting";
}

/// <summary>The outcome of a transaction.</summary>
/// <param name="Manifest">The resulting manifest, or null when nothing is installed.</param>
/// <param name="Notice">A user-facing summary, including settings that were unavailable.</param>
internal sealed record GameToolOutcome(GameToolManifest? Manifest, string Notice);

/// <summary>A refusal or failure whose message is meant for the user.</summary>
internal sealed class GameToolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Installs, updates, reconfigures and removes a tool in a game folder as a recoverable transaction.</summary>
/// <remarks>
///     <para>
///         Every change is planned first. Existing files are classified: absent files are created; files
///         WSGM owns are replaced; game files matching the tool's replaceable patterns are backed up and
///         replaced; an unmanaged copy of the tool, another mod or any other file is a conflict and the
///         install is refused. A pending manifest carrying the full journal is persisted before the first
///         game file changes, so a crash leaves enough to roll back on the next open.
///     </para>
///     <para>
///         Files are written through a temporary sibling and moved into place, so each game file is
///         either the old or the new content. Owned files replaced by an update are backed up for the
///         transaction, so a failed update restores the previous binaries. IO and access failures are not
///         retried; they roll back and report that a file is in use or protected.
///     </para>
/// </remarks>
internal sealed class GameToolInstaller(string userRoot)
{
    /// <summary>Inspects the target directory without changing anything.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="targetDirectory">The absolute target directory.</param>
    /// <param name="manifest">The installed manifest, or null.</param>
    /// <returns>The inspection.</returns>
    internal static GameToolInspection Inspect(GameToolDefinition tool, string targetDirectory,
        GameToolManifest? manifest)
    {
        var owned = new HashSet<string>(manifest?.Files.Select(file => file.RelativePath) ?? [],
            StringComparer.OrdinalIgnoreCase);
        List<GameToolUnmanagedCopy> unmanaged = [];
        foreach (var relative in DetectionCandidates(tool, targetDirectory))
        {
            if (owned.Contains(relative))
            {
                continue;
            }

            var info = PeImage.TryVersion(Path.Combine(targetDirectory, relative));
            if (info is not null && GameToolIdentityCheck.IsTool(tool, info))
            {
                unmanaged.Add(new GameToolUnmanagedCopy(relative,
                    info.ProductVersion.Length > 0 ? info.ProductVersion : info.FileVersion));
            }
        }

        List<GameToolProxyStatus> proxies = [];
        foreach (var option in tool.Proxy.Options)
        {
            var path = Path.Combine(targetDirectory, option.Name);
            if (manifest?.ProxyName.Equals(option.Name, StringComparison.OrdinalIgnoreCase) == true
                || !File.Exists(path))
            {
                proxies.Add(new GameToolProxyStatus(option.Name, option.Hint, true, null, option.Suggest));
                continue;
            }

            proxies.Add(new GameToolProxyStatus(option.Name, option.Hint, false,
                DescribeExisting(tool, path, owned.Contains(option.Name)), option.Suggest));
        }

        List<string> missing = [];
        List<string> modified = [];
        if (manifest is { State: GameToolManifestState.Installed })
        {
            foreach (var file in manifest.Files)
            {
                var info = new FileInfo(Path.Combine(targetDirectory, file.RelativePath));
                if (!info.Exists)
                {
                    missing.Add(file.RelativePath);
                }
                else if (file.Kind == GameToolFileKind.Binary
                         && (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc))
                {
                    modified.Add(file.RelativePath);
                }
            }
        }

        return new GameToolInspection(unmanaged, proxies, missing, modified);
    }

    /// <summary>
    ///     The first offered proxy name that is free and may be suggested, starting at the preferred one and
    ///     wrapping around.
    /// </summary>
    /// <param name="proxies">The inspection's proxy states.</param>
    /// <param name="preferred">The preferred name.</param>
    /// <returns>A free name, or the preferred one when none is free.</returns>
    internal static string SuggestProxy(IReadOnlyList<GameToolProxyStatus> proxies, string preferred)
    {
        var start = Math.Max(0, proxies.ToList().FindIndex(proxy =>
            proxy.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)));
        for (var offset = 0; offset < proxies.Count; offset++)
        {
            var candidate = proxies[(start + offset) % proxies.Count];
            if (candidate is { Available: true, Suggest: true })
            {
                return candidate.Name;
            }
        }

        return preferred;
    }

    /// <summary>Explains why a tool can't be installed for a runtime architecture, or returns null.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="architecture">The runtime executable's architecture.</param>
    /// <returns>A user-facing reason, or null when the architecture is supported.</returns>
    internal static string? Incompatible(GameToolDefinition tool, string architecture)
    {
        if (tool.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            return null;
        }

        var label = PeImage.Label(architecture);
        return tool.Name + " doesn't support " + (label.Length > 0 ? label + " games" : "this game's program") + ".";
    }

    /// <summary>Describes why a target directory can't be written to, or null when it can.</summary>
    /// <param name="directory">The target directory.</param>
    /// <returns>A user-facing reason, or null.</returns>
    /// <remarks>
    ///     Creates, renames and deletes a uniquely named probe file, as the install would. Protected folders such
    ///     as WindowsApps are refused earlier by <see cref="GameFolderGuard.Reject" />.
    /// </remarks>
    internal static string? ProbeWritable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "The game's folder is not available.";
        }

        var probe = Path.Combine(directory, ".wsgm-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        var renamed = Path.ChangeExtension(probe, ".renamed.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Move(probe, renamed);
            File.Delete(renamed);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "WSGM can't write to the game's folder. It may be protected or on a read-only drive.";
        }
        finally
        {
            FileCleanup.TryDelete(probe);
            FileCleanup.TryDelete(renamed);
        }
    }

    /// <summary>Installs, updates, repairs or reinstalls a tool from a verified package.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="target">Where to install.</param>
    /// <param name="package">The verified package.</param>
    /// <param name="request">What to do.</param>
    /// <param name="current">The current manifest, or null for a fresh install.</param>
    /// <param name="ensureClosed">
    ///     Rechecks right before the first game-file write that the game is closed; returns why not, or null.
    /// </param>
    /// <param name="phase">Receives the current phase text.</param>
    /// <param name="cancellationToken">Cancels planning; once files are being written the transaction completes or rolls back.</param>
    /// <param name="inspection">
    ///     The caller's inspection of the target directory with <paramref name="current" />, reused instead of
    ///     inspecting again; null to inspect here.
    /// </param>
    /// <returns>The outcome.</returns>
    internal async Task<GameToolOutcome> InstallAsync(GameToolDefinition tool, GameToolTarget target,
        GameToolPackage package, GameToolRequest request, GameToolManifest? current, Func<string?> ensureClosed,
        Action<string> phase, CancellationToken cancellationToken, GameToolInspection? inspection = null)
    {
        if (Incompatible(tool, target.Architecture) is { } incompatible)
        {
            throw new GameToolException(incompatible);
        }

        if (package.Architecture != target.Architecture)
        {
            throw new GameToolException("The downloaded " + tool.Name + " does not match the game's architecture.");
        }

        if (tool.Proxy.Options.All(option => !option.Name.Equals(request.ProxyName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GameToolException("Choose one of the offered file names.");
        }

        if (current is not null && current.Family.Length > 0 && package.Family.Length > 0
            && current.Family != package.Family && !request.ReplaceConfig)
        {
            throw new GameToolException("This update changes " + tool.Name + "'s configuration format. Confirm "
                                        + "that WSGM may replace " + tool.PrimaryConfig + " and re-apply your settings.");
        }

        phase("Checking the game folder");
        var directory = target.TargetDirectory;
        var proxy = tool.Proxy.Options.First(option =>
            option.Name.Equals(request.ProxyName, StringComparison.OrdinalIgnoreCase)).Name;
        var plan = await Task.Run(() => Plan(tool, directory, package, proxy, request, current,
            inspection ?? Inspect(tool, directory, current)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireWritable(target);
        var installId = current?.InstallId ?? Guid.NewGuid().ToString("N");
        var transaction = "tx-" + Guid.NewGuid().ToString("N");
        var originals = GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id, installId);
        var temporary = GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id, transaction);
        var staging = Path.Combine(temporary, ".wsgm-staged");

        // A freshly copied configuration file gets WSGM's settings before it reaches the game, so the
        // journal records its final content and a rollback recognises the file as WSGM's own.
        // A kept configuration file carries the user's own edits; settings are only written into a
        // freshly copied file or when the user changed them.
        var settings = MergeSettings(tool, current?.Settings, request.Settings);
        var configWritten = plan.Items.Any(item => item.Kind == GameToolFileKind.Config
                                                   && item.Action != GameToolJournalAction.Delete);
        List<string> unavailable = [];
        var configIndex = plan.Items.FindIndex(item => !item.Restore && item.Action != GameToolJournalAction.Delete
                                                                     && item.Relative.Equals(tool.PrimaryConfig,
                                                                         StringComparison.OrdinalIgnoreCase));
        if (configIndex >= 0 && tool.Settings.Length > 0)
        {
            var item = plan.Items[configIndex];
            var staged = Path.Combine(staging, tool.PrimaryConfig);
            Directory.CreateDirectory(staging);
            File.Copy(item.Source!, staged, true);
            File.SetAttributes(staged, File.GetAttributes(staged) & ~FileAttributes.ReadOnly);
            unavailable = ApplySettings(tool, staging, settings);
            plan.Items[configIndex] = item with { Source = staged, NewSha256 = Sha256(staged) };
        }

        List<GameToolJournalEntry> journal = [];
        List<GameToolReplacedFile> newOriginals = [];
        foreach (var item in plan.Items)
        {
            var backup = item.Action == GameToolJournalAction.Create
                ? ""
                : Path.Combine(item.Original ? originals : temporary, item.Relative.Replace('/', '\\'));
            journal.Add(new GameToolJournalEntry
            {
                RelativePath = item.Relative, Action = item.Action, NewSha256 = item.NewSha256, BackupPath = backup,
                PreviousSha256 = item.PreviousSha256
            });
            if (item.Original)
            {
                newOriginals.Add(new GameToolReplacedFile
                {
                    RelativePath = item.Relative, OriginalSha256 = item.PreviousSha256, BackupPath = backup
                });
            }
        }

        var now = DateTime.UtcNow;
        var previous = current is null ? null : current with { Previous = null, Journal = [], JournalDirectories = [] };
        var pending = new GameToolManifest
        {
            ToolId = tool.Id, InstallId = installId, Version = package.Version, Family = package.Family,
            ReleaseId = package.Release.Id, ReleaseVersion = package.Release.Version,
            SourceUrl = package.Release.Asset.Url, AssetName = package.Release.Asset.Name,
            AssetSha256 = package.Release.Asset.Sha256,
            ProxyName = proxy, InstallRoot = target.InstallRoot, TargetDirectory = target.RelativeTargetDirectory,
            RuntimeExecutable = GameToolPaths.Relative(target.InstallRoot, target.RuntimeExecutable),
            Architecture = target.Architecture, Engine = target.Engine, Files = current?.Files ?? [],
            Replaced = current?.Replaced ?? [], CreatedDirectories = current?.CreatedDirectories ?? [],
            Settings = current?.Settings ?? new Dictionary<string, string>(StringComparer.Ordinal),
            InstalledUtc = current?.InstalledUtc ?? now, UpdatedUtc = now, State = GameToolManifestState.Pending,
            Journal = journal, JournalDirectories = [.. plan.Directories], Previous = previous,
            Operation = request.Operation
        };

        // Persist BEFORE touching the game: after a crash, this journal is the only way back.
        Persist(target.AppId, pending);
        try
        {
            RequireClosed(ensureClosed);
            phase("Copying files");
            await Task.Run(() => Execute(directory, plan, journal), CancellationToken.None).ConfigureAwait(false);
            if (configIndex < 0 && (configWritten || request.Settings.Count > 0))
            {
                // Only a kept file the user asked to change is edited in place.
                phase("Applying settings");
                unavailable = ApplySettings(tool, directory, settings);
            }

            var files = BuildOwned(tool, directory, plan, current);
            var restored = plan.Items.Where(item => item.Restore).Select(item => item.Relative)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var installed = pending with
            {
                Files = files,
                Replaced = [.. (current?.Replaced ?? []).Where(original => !restored.Contains(original.RelativePath)
                    && newOriginals.All(added =>
                        !added.RelativePath.Equals(original.RelativePath, StringComparison.OrdinalIgnoreCase))), .. newOriginals],
                CreatedDirectories = [.. (current?.CreatedDirectories ?? []).Union(plan.Directories,
                    StringComparer.OrdinalIgnoreCase)],
                Settings = settings, State = GameToolManifestState.Installed, Journal = [], JournalDirectories = [],
                Previous = null, Operation = ""
            };
            Persist(target.AppId, installed);
            GameToolStorage.DeleteTree(temporary);
            var notice = request.Operation switch
            {
                "update" => tool.Name + " updated to " + GameToolVersions.NumericToken(package.Version) + ".",
                "repair" => tool.Name + " repaired.",
                _ => tool.Name + " " + GameToolVersions.NumericToken(package.Version) + " installed as " + proxy + "."
            };
            if (unavailable.Count > 0)
            {
                notice += " Not available in this version: " + string.Join(", ", unavailable) + ".";
            }

            Log.Info($"Game Tools: {request.Operation} {tool.Id} {package.Version} for {target.AppId} as {proxy}: "
                     + $"{plan.Items.Count} file changes.");
            return new GameToolOutcome(installed, notice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var undone = await Task.Run(() => Rollback(target.AppId, directory, pending), CancellationToken.None)
                .ConfigureAwait(false);
            GameToolStorage.DeleteTree(staging);
            Log.Warn($"Game Tools: {request.Operation} {tool.Id} for {target.AppId} failed and was "
                     + (undone ? "rolled back" : "only partly rolled back") + $": {ex.Message}");
            throw Translate(ex, undone);
        }
    }

    /// <summary>Renames the installed proxy and applies settings.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="target">The install target.</param>
    /// <param name="current">The installed manifest.</param>
    /// <param name="proxyName">The new proxy name, or null to keep it.</param>
    /// <param name="settings">Changed settings.</param>
    /// <param name="ensureClosed">Rechecks right before writing that the game is closed; returns why not, or null.</param>
    /// <returns>The outcome.</returns>
    internal GameToolOutcome Configure(GameToolDefinition tool, GameToolTarget target, GameToolManifest current,
        string? proxyName, IReadOnlyDictionary<string, string> settings, Func<string?> ensureClosed)
    {
        var directory = target.TargetDirectory;
        RequireWritable(target);
        var manifest = current;
        var notices = new List<string>();
        if (proxyName is not null && !proxyName.Equals(current.ProxyName, StringComparison.OrdinalIgnoreCase))
        {
            var option = tool.Proxy.Options.FirstOrDefault(item =>
                             item.Name.Equals(proxyName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new GameToolException("Choose one of the offered file names.");
            var source = Path.Combine(directory, current.ProxyName);
            var destination = Path.Combine(directory, option.Name);
            if (!File.Exists(source))
            {
                throw new GameToolException(tool.Name + "'s file is missing. Use Repair first.");
            }

            if (File.Exists(destination))
            {
                throw new GameToolException(DescribeExisting(tool, destination, false));
            }

            var sha = Sha256(source);
            var transaction = GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id,
                "tx-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(transaction, current.ProxyName);
            List<GameToolJournalEntry> journal =
            [
                new() { RelativePath = option.Name, Action = GameToolJournalAction.Create, NewSha256 = sha },
                new()
                {
                    RelativePath = current.ProxyName, Action = GameToolJournalAction.Delete, BackupPath = backup,
                    PreviousSha256 = sha
                }
            ];
            var pending = current with
            {
                State = GameToolManifestState.Pending, Journal = journal, JournalDirectories = [],
                Previous = current with { Previous = null }, Operation = "configure"
            };
            Persist(target.AppId, pending);
            try
            {
                RequireClosed(ensureClosed);
                Directory.CreateDirectory(transaction);
                File.Copy(source, backup, true);
                CopyInto(source, destination);
                ClearReadOnly(source);
                File.Delete(source);
                var info = new FileInfo(destination);
                manifest = current with
                {
                    ProxyName = option.Name,
                    Files =
                    [
                        .. current.Files.Where(file =>
                            !file.RelativePath.Equals(current.ProxyName, StringComparison.OrdinalIgnoreCase)),
                        new GameToolOwnedFile
                        {
                            RelativePath = option.Name, Sha256 = sha, Size = info.Length,
                            LastWriteUtc = info.LastWriteTimeUtc, Kind = GameToolFileKind.Binary
                        }
                    ],
                    UpdatedUtc = DateTime.UtcNow
                };
                Persist(target.AppId, manifest);
                GameToolStorage.DeleteTree(transaction);
                notices.Add(tool.Name + " now loads as " + option.Name + ".");
                Log.Info($"Game Tools: renamed {tool.Id} for {target.AppId} from {current.ProxyName} to {option.Name}.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var undone = Rollback(target.AppId, directory, pending);
                Log.Warn($"Game Tools: renaming {tool.Id} for {target.AppId} failed: {ex.Message}");
                throw Translate(ex, undone);
            }
        }

        if (settings.Count > 0)
        {
            RequireClosed(ensureClosed);
            var merged = MergeSettings(tool, manifest.Settings, settings);
            List<string> unavailable;
            try
            {
                unavailable = ApplySettings(tool, directory, merged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GameToolException(tool.PrimaryConfig + " is in use or protected. Close the game and try again.",
                    ex);
            }

            manifest = manifest with { Settings = merged, UpdatedUtc = DateTime.UtcNow };
            Persist(target.AppId, manifest);
            notices.Add(unavailable.Count > 0
                ? "Settings saved. Not available in this version: " + string.Join(", ", unavailable) + "."
                : "Settings saved.");
            Log.Info($"Game Tools: configured {tool.Id} for {target.AppId}.");
        }

        return new GameToolOutcome(manifest, string.Join(" ", notices));
    }

    /// <summary>Removes an installation, restoring backed-up game files.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="target">The install target.</param>
    /// <param name="current">The manifest.</param>
    /// <returns>The outcome; a remaining manifest means some files could not be removed yet.</returns>
    internal GameToolOutcome Remove(GameToolDefinition tool, GameToolTarget target, GameToolManifest current)
    {
        var directory = target.TargetDirectory;
        if (Unreachable(directory))
        {
            // Missing files on a disconnected drive are not removed files; keep the manifest for later.
            return new GameToolOutcome(current, "The folder " + directory + " is on a drive that isn't connected. "
                                                + "Connect it and remove again.");
        }

        if (current.State != GameToolManifestState.Installed)
        {
            // The user asked for this, so files someone else changed since are left in place and forgotten
            // instead of blocking the undo forever.
            List<string> changed = [];
            var undone = Rollback(target.AppId, directory, current, changed);
            var message = undone
                ? "The interrupted change was undone."
                : "Some files couldn't be restored. Close the game and try again.";
            if (changed.Count > 0)
            {
                message += " Left in place because they changed since: " + string.Join(", ", changed) + ".";
            }

            return new GameToolOutcome(undone ? GameToolStorage.Read(userRoot).Find(target.AppId)?.Tools
                .FirstOrDefault(manifest => manifest.ToolId == tool.Id) : current, message);
        }

        List<GameToolOwnedFile> remaining = [];
        List<string> left = [];
        var locked = 0;
        foreach (var file in current.Files)
        {
            var path = Path.Combine(directory, file.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (file.Kind == GameToolFileKind.Binary && !Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    left.Add(file.RelativePath);
                    continue;
                }

                ClearReadOnly(path);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(file);
                locked++;
            }
        }

        List<GameToolReplacedFile> unrestored = [];
        foreach (var original in current.Replaced)
        {
            var path = Path.Combine(directory, original.RelativePath);
            if (!File.Exists(original.BackupPath))
            {
                left.Add(original.RelativePath + " (backup missing)");
                continue;
            }

            if (File.Exists(path))
            {
                unrestored.Add(original);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                CopyInto(original.BackupPath, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unrestored.Add(original);
                locked++;
            }
        }

        foreach (var artifact in tool.RuntimeArtifacts)
        {
            var path = Path.Combine(directory, artifact);
            if (File.Exists(path))
            {
                try
                {
                    ClearReadOnly(path);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left.Add(artifact);
                }
            }
        }

        DeleteEmptyDirectories(directory, current.CreatedDirectories);
        GameToolManifest? manifest = null;
        string notice;
        if (remaining.Count > 0 || (locked > 0 && unrestored.Count > 0))
        {
            manifest = current with { Files = remaining, Replaced = unrestored, UpdatedUtc = DateTime.UtcNow };
            notice = "Some files are in use or protected. Close the game and remove again.";
        }
        else
        {
            notice = tool.Name + " removed.";
            if (unrestored.Count == 0)
            {
                GameToolStorage.DeleteTree(GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id, current.InstallId));
            }
            else
            {
                notice += " Some original files were changed since and were not restored; their backups are in "
                          + GameToolStorage.BackupRoot(userRoot, target.AppId, tool.Id, current.InstallId) + ".";
            }
        }

        if (left.Count > 0)
        {
            notice += " Left in place because they changed after installation: " + string.Join(", ", left) + ".";
        }

        Persist(target.AppId, manifest, tool.Id);
        Log.Info($"Game Tools: removed {tool.Id} for {target.AppId}: {(manifest is null ? "complete" : "partial")}, "
                 + $"{left.Count} left, {locked} locked.");
        return new GameToolOutcome(manifest, notice);
    }

    /// <summary>Undoes an interrupted transaction from its journal.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="directory">The absolute target directory.</param>
    /// <param name="pending">The pending or failed manifest.</param>
    /// <param name="forgotten">
    ///     When the user asked for the undo, receives files changed by someone else, which are then left in
    ///     place instead of failing the rollback; null for automatic recovery.
    /// </param>
    /// <returns>True when everything was undone and the previous manifest restored.</returns>
    /// <remarks>
    ///     Each entry is undone only when the file is in a state the transaction could have produced:
    ///     the new content, the old content, or absent. Anything else was changed by someone else and is
    ///     left alone; the manifest then becomes <see cref="GameToolManifestState.Failed" /> and waits for
    ///     the user instead of being retried automatically. A target folder on a disconnected drive is
    ///     not rolled back and leaves the manifest unchanged.
    /// </remarks>
    internal bool Rollback(uint appId, string directory, GameToolManifest pending, List<string>? forgotten = null)
    {
        if (Unreachable(directory))
        {
            Log.Warn($"Game Tools: rollback of {pending.ToolId} for {appId} waits for {directory} to be connected.");
            return false;
        }

        List<GameToolJournalEntry> failed = [];
        List<GameToolJournalEntry> kept = [];
        foreach (var entry in Enumerable.Reverse(pending.Journal))
        {
            var path = Path.Combine(directory, entry.RelativePath);
            try
            {
                foreach (var stray in StrayTemporaries(path))
                {
                    FileCleanup.TryDelete(stray);
                }

                var exists = File.Exists(path);
                var hash = exists ? Sha256(path) : "";
                switch (entry.Action)
                {
                    case GameToolJournalAction.Create:
                        if (exists && hash.Equals(entry.NewSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            ClearReadOnly(path);
                            File.Delete(path);
                        }
                        else if (exists)
                        {
                            (forgotten is null ? failed : kept).Add(entry);
                        }

                        break;
                    case GameToolJournalAction.Replace:
                    case GameToolJournalAction.Delete:
                        if (!File.Exists(entry.BackupPath))
                        {
                            // The backup is taken before the file changes, so the file is untouched.
                            break;
                        }

                        if (exists && hash.Equals(entry.PreviousSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        if (!exists || hash.Equals(entry.NewSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            CopyInto(entry.BackupPath, path);
                        }
                        else
                        {
                            (forgotten is null ? failed : kept).Add(entry);
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(entry);
            }
        }

        DeleteEmptyDirectories(directory, pending.JournalDirectories);
        if (failed.Count > 0)
        {
            Persist(appId, pending with { State = GameToolManifestState.Failed, Journal = failed });
            Log.Warn($"Game Tools: rollback of {pending.ToolId} for {appId} left {failed.Count} files unresolved.");
            return false;
        }

        Persist(appId, pending.Previous, pending.ToolId);
        if (kept.Count > 0)
        {
            // Their backups are the only copy of what was there before, so every backup stays on disk.
            forgotten!.AddRange(kept.Select(entry => entry.RelativePath));
            Log.Warn($"Game Tools: rollback of {pending.ToolId} for {appId} left {kept.Count} changed files in "
                     + $"place; backups kept in {GameToolStorage.BackupRoot(userRoot, appId, pending.ToolId, "")}.");
            Log.Info($"Game Tools: rolled back {pending.Operation} of {pending.ToolId} for {appId}.");
            return true;
        }

        foreach (var backups in pending.Journal.Select(entry => entry.BackupPath)
                     .Where(path => path.Contains(@"\tx-", StringComparison.Ordinal))
                     .Select(path => path[..(path.IndexOf(@"\tx-", StringComparison.Ordinal) + 36)])
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            GameToolStorage.DeleteTree(backups);
        }

        if (pending.Previous is null)
        {
            GameToolStorage.DeleteTree(GameToolStorage.BackupRoot(userRoot, appId, pending.ToolId, pending.InstallId));
        }

        Log.Info($"Game Tools: rolled back {pending.Operation} of {pending.ToolId} for {appId}.");
        return true;
    }

    /// <summary>Reads the current setting values from the installed configuration file.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="directory">The target directory.</param>
    /// <returns>Raw INI values keyed by setting id; a missing key has no entry. Null when the file is missing.</returns>
    internal static Dictionary<string, string>? ReadSettings(GameToolDefinition tool, string directory)
    {
        var path = Path.Combine(directory, tool.PrimaryConfig);
        if (tool.PrimaryConfig.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var values = GameToolIni.Read(path, tool.Settings.Select(setting => (setting.Section, setting.Key)));
            return tool.Settings.Where(setting => values.ContainsKey(GameToolIni.Id(setting.Section, setting.Key)))
                .ToDictionary(setting => setting.Id, setting => values[GameToolIni.Id(setting.Section, setting.Key)],
                    StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> DetectionCandidates(GameToolDefinition tool, string directory)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tool.Proxy.DetectionNames)
        {
            if (File.Exists(Path.Combine(directory, name)) && seen.Add(name))
            {
                yield return name;
            }
        }

        foreach (var pattern in tool.Proxy.DetectionPatterns)
        {
            var folder = pattern.Contains('/') ? pattern[..pattern.LastIndexOf('/')] : "";
            var mask = pattern.Contains('/') ? pattern[(pattern.LastIndexOf('/') + 1)..] : pattern;
            var path = Path.Combine(directory, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, mask, SearchOption.TopDirectoryOnly).Take(400).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var relative = GameToolPattern.Normalize(Path.GetRelativePath(directory, file));
                if (seen.Add(relative))
                {
                    yield return relative;
                }
            }
        }
    }

    private static string DescribeExisting(GameToolDefinition tool, string path, bool owned)
    {
        if (owned)
        {
            return "Already used by another file of " + tool.Name + ".";
        }

        var info = PeImage.TryVersion(path);
        if (info is not null && GameToolIdentityCheck.IsTool(tool, info))
        {
            return tool.Name + " installed outside WSGM uses this name.";
        }

        if (info is not null && GameToolIdentityCheck.OtherTool(tool, info) is { } other)
        {
            return other + " uses this name.";
        }

        return "The game or another mod already has a file with this name.";
    }

    // Restore marks a game original copied back from its backup: it is no longer owned or replaced.
    private sealed record PlanItem(string Relative, string? Source, GameToolJournalAction Action, bool Original,
        string NewSha256, string PreviousSha256, GameToolFileKind Kind, bool Restore = false);

    // Unchanged maps each file the plan leaves alone to its hash when planning already read it, else null.
    private sealed record InstallPlan(List<PlanItem> Items, List<string> Directories,
        Dictionary<string, string?> Unchanged);

    private static InstallPlan Plan(GameToolDefinition tool, string directory, GameToolPackage package, string proxy,
        GameToolRequest request, GameToolManifest? current, GameToolInspection inspection)
    {
        var owned = (current?.Files ?? []).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var replaced = (current?.Replaced ?? []).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        List<PlanItem> items = [];
        List<string> directories = [];
        List<string> conflicts = [];
        Dictionary<string, string?> unchanged = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

        // An unmanaged installation is either a conflict or, when the user asked, backed up and replaced.
        HashSet<string> clearing = new(StringComparer.OrdinalIgnoreCase);
        if (inspection.Unmanaged.Count > 0)
        {
            if (!request.ReplaceExisting)
            {
                throw new GameToolException(tool.Name + " is already installed outside WSGM ("
                                            + string.Join(", ", inspection.Unmanaged.Select(copy => copy.RelativePath))
                                            + "). Use \"Replace existing installation\" to let WSGM take over.");
            }

            foreach (var copy in inspection.Unmanaged)
            {
                clearing.Add(copy.RelativePath);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetFileName(file);
                if (!owned.ContainsKey(relative) && GameToolPattern.MatchesAny(tool.LegacyFiles, relative))
                {
                    clearing.Add(relative);
                }
            }
        }

        foreach (var source in Directory.EnumerateFiles(package.Root, "*", SearchOption.AllDirectories))
        {
            var payloadRelative = GameToolPattern.Normalize(Path.GetRelativePath(package.Root, source));
            if (tool.IsExcluded(payloadRelative))
            {
                continue;
            }

            var relative = payloadRelative.Equals(tool.PrimaryFile, StringComparison.OrdinalIgnoreCase)
                ? proxy
                : payloadRelative;
            var destination = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', '\\')));
            if (!StoragePaths.IsUnder(directory, destination))
            {
                throw new GameToolException("The package contains a path outside the game folder.");
            }

            written.Add(relative);
            var kind = tool.IsConfig(relative) ? GameToolFileKind.Config : GameToolFileKind.Binary;
            AddDirectories(directory, relative, directories);
            var exists = File.Exists(destination);
            if (!exists)
            {
                if (Directory.Exists(destination))
                {
                    conflicts.Add("A folder has the name of a file.");
                    continue;
                }

                items.Add(new PlanItem(relative, source, GameToolJournalAction.Create, false,
                    package.Sha256(payloadRelative, source), "", kind));
                continue;
            }

            if (kind == GameToolFileKind.Config && owned.ContainsKey(relative) && !request.ReplaceConfig)
            {
                unchanged[relative] = null;
                continue;
            }

            var previous = Sha256(destination);
            var newSha = package.Sha256(payloadRelative, source);
            if (owned.TryGetValue(relative, out var ownedFile))
            {
                if (!previous.Equals(newSha, StringComparison.OrdinalIgnoreCase))
                {
                    // A game update rewrote a file whose original WSGM backed up: the game's new content is
                    // the original now, so it replaces the old backup instead of being discarded.
                    var rewritten = kind == GameToolFileKind.Binary && replaced.ContainsKey(relative)
                                                                    && ownedFile.Sha256.Length > 0
                                                                    && !previous.Equals(ownedFile.Sha256,
                                                                        StringComparison.OrdinalIgnoreCase);
                    items.Add(new PlanItem(relative, source, GameToolJournalAction.Replace, rewritten, newSha, previous,
                        kind));
                }
                else
                {
                    unchanged[relative] = previous;
                }

                continue;
            }

            if (replaced.ContainsKey(relative))
            {
                // A game original backed up by an earlier version: replace it again under that backup.
                items.Add(new PlanItem(relative, source, GameToolJournalAction.Replace, false, newSha, previous, kind));
                continue;
            }

            if (clearing.Contains(relative) || tool.IsReplaceable(relative))
            {
                items.Add(new PlanItem(relative, source, GameToolJournalAction.Replace, true, newSha, previous, kind));
                continue;
            }

            if (relative.Equals(proxy, StringComparison.OrdinalIgnoreCase))
            {
                var suggestion = SuggestProxy(inspection.Proxies, proxy);
                conflicts.Add(DescribeExisting(tool, destination, false)
                              + (suggestion.Equals(proxy, StringComparison.OrdinalIgnoreCase) ? "" : " Try " + suggestion + "."));
            }
            else
            {
                conflicts.Add(relative + " already exists and is not a file " + tool.Name + " may replace.");
            }
        }

        // Unmanaged copies and legacy files the new payload does not overwrite are backed up and removed.
        foreach (var relative in clearing.Where(path => !written.Contains(path)))
        {
            var path = Path.Combine(directory, relative);
            items.Add(new PlanItem(relative, null, GameToolJournalAction.Delete, true, "", Sha256(path),
                GameToolFileKind.Binary));
        }

        // Owned files the new version no longer ships are removed, recoverable for the transaction. One that
        // replaced a game file gets that original back from its backup instead.
        foreach (var file in owned.Values.Where(file => !written.Contains(file.RelativePath)))
        {
            var path = Path.Combine(directory, file.RelativePath);
            var exists = File.Exists(path);
            if (replaced.TryGetValue(file.RelativePath, out var original) && File.Exists(original.BackupPath))
            {
                items.Add(new PlanItem(file.RelativePath, original.BackupPath,
                    exists ? GameToolJournalAction.Replace : GameToolJournalAction.Create, false,
                    Sha256(original.BackupPath), exists ? Sha256(path) : "", file.Kind, true));
                AddDirectories(directory, file.RelativePath, directories);
            }
            else if (exists && file.Kind == GameToolFileKind.Binary)
            {
                items.Add(new PlanItem(file.RelativePath, null, GameToolJournalAction.Delete, false, "", Sha256(path),
                    file.Kind));
            }
        }

        if (conflicts.Count > 0)
        {
            throw new GameToolException(string.Join(" ", conflicts.Distinct()));
        }

        return new InstallPlan(items, directories, unchanged);
    }

    private static void AddDirectories(string root, string relative, List<string> created)
    {
        var parts = relative.Split('/');
        for (var depth = 1; depth < parts.Length; depth++)
        {
            var folder = string.Join('/', parts[..depth]);
            if (!Directory.Exists(Path.Combine(root, folder)) && !created.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                created.Add(folder);
            }
        }
    }

    private static void Execute(string directory, InstallPlan plan, IReadOnlyList<GameToolJournalEntry> journal)
    {
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            var entry = journal[index];
            var path = Path.Combine(directory, item.Relative.Replace('/', '\\'));
            if (item.Action != GameToolJournalAction.Create)
            {
                // Back up first: until the backup exists the game file is untouched.
                Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath)!);
                File.Copy(path, entry.BackupPath, true);
                File.SetAttributes(entry.BackupPath, File.GetAttributes(entry.BackupPath) & ~FileAttributes.ReadOnly);
            }

            if (item.Action == GameToolJournalAction.Delete)
            {
                ClearReadOnly(path);
                File.Delete(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CopyInto(item.Source!, path);
        }
    }

    private static List<GameToolOwnedFile> BuildOwned(GameToolDefinition tool, string directory, InstallPlan plan,
        GameToolManifest? current)
    {
        Dictionary<string, GameToolOwnedFile> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in current?.Files ?? [])
        {
            if (plan.Unchanged.ContainsKey(file.RelativePath))
            {
                files[file.RelativePath] = file;
            }
        }

        foreach (var item in plan.Items.Where(item => item.Action != GameToolJournalAction.Delete && !item.Restore))
        {
            files[item.Relative] = Owned(directory, item.Relative, item.NewSha256, item.Kind);
        }

        // Files the plan left unchanged because they already matched are still owned; refresh their stamps.
        // Planning hashed them already and the transaction didn't touch them.
        foreach (var (relative, sha) in plan.Unchanged.Where(entry => !files.ContainsKey(entry.Key)
                                                                       || files[entry.Key].Kind == GameToolFileKind.Binary))
        {
            var path = Path.Combine(directory, relative);
            if (File.Exists(path))
            {
                files[relative] = Owned(directory, relative, sha ?? Sha256(path),
                    tool.IsConfig(relative) ? GameToolFileKind.Config : GameToolFileKind.Binary);
            }
        }

        return [.. files.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    private static GameToolOwnedFile Owned(string directory, string relative, string sha, GameToolFileKind kind)
    {
        var info = new FileInfo(Path.Combine(directory, relative));
        return new GameToolOwnedFile
        {
            RelativePath = relative, Sha256 = sha, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Kind = kind
        };
    }

    private static Dictionary<string, string> MergeSettings(GameToolDefinition tool,
        IReadOnlyDictionary<string, string>? current, IReadOnlyDictionary<string, string> requested)
    {
        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        foreach (var setting in tool.Settings)
        {
            var value = requested.GetValueOrDefault(setting.Id) ?? current?.GetValueOrDefault(setting.Id) ?? setting.Default;
            if (setting.Options.All(option => option.Value != value))
            {
                throw new GameToolException("The value for " + setting.Label + " is not one of its options.");
            }

            merged[setting.Id] = value;
        }

        return merged;
    }

    private static List<string> ApplySettings(GameToolDefinition tool, string directory,
        IReadOnlyDictionary<string, string> settings)
    {
        var path = Path.Combine(directory, tool.PrimaryConfig);
        if (tool.Settings.Length == 0 || !File.Exists(path))
        {
            return [];
        }

        List<GameToolIniEdit> edits = [];
        foreach (var setting in tool.Settings)
        {
            var option = setting.Options.First(item => item.Value == settings[setting.Id]);
            edits.Add(new GameToolIniEdit(setting.Section, setting.Key, option.Ini));
        }

        var missing = GameToolIni.Apply(path, edits);
        return [.. tool.Settings.Where(setting => missing.Any(edit => edit.Key == setting.Key && edit.Section == setting.Section))
            .Select(setting => setting.Label)];
    }

    private void Persist(uint appId, GameToolManifest? manifest, string? toolId = null)
    {
        var id = manifest?.ToolId ?? toolId ?? throw new ArgumentNullException(nameof(toolId));
        GameToolStorage.Mutate(userRoot, store =>
        {
            var record = store.Find(appId) ?? new GameToolRecord { AppId = appId };
            List<GameToolManifest> tools = [.. record.Tools.Where(tool => tool.ToolId != id)];
            if (manifest is not null)
            {
                tools.Add(manifest);
            }

            return store.With(appId, record with { Tools = tools });
        });
    }

    private static Exception Translate(Exception ex, bool undone)
    {
        var suffix = undone ? " Nothing was changed." : " Some files couldn't be restored; use Remove to finish undoing it.";
        return ex switch
        {
            GameToolException known => new GameToolException(known.Message + (undone ? "" : suffix), known),
            IOException or UnauthorizedAccessException => new GameToolException(
                "A game file is in use or protected. Close the game and try again." + suffix, ex),
            _ => new GameToolException("The change failed: " + ex.Message + suffix, ex)
        };
    }

    private static void CopyInto(string source, string destination)
    {
        var temporary = destination + ".wsgm-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.Copy(source, temporary, true);
            File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
            if (File.Exists(destination))
            {
                ClearReadOnly(destination);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            FileCleanup.TryDelete(temporary);
        }
    }

    private static IEnumerable<string> StrayTemporaries(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, Path.GetFileName(path) + ".wsgm-*.tmp");
    }

    // A missing folder on a present drive is really gone; one on a disconnected drive or share is not.
    private static bool Unreachable(string directory)
    {
        return !Directory.Exists(directory) && Path.GetPathRoot(directory) is { Length: > 0 } root
                                            && !Directory.Exists(root);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void DeleteEmptyDirectories(string root, IEnumerable<string> directories)
    {
        foreach (var relative in directories.OrderByDescending(path => path.Count(character => character == '/'))
                     .ThenByDescending(path => path.Length))
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder the game started using is left in place.
            }
        }
    }

    // Refuses protected or shared folders and folders WSGM can't write before anything is persisted.
    private static void RequireWritable(GameToolTarget target)
    {
        if ((GameFolderGuard.Reject(target.RuntimeExecutable) ?? ProbeWritable(target.TargetDirectory)) is { } reason)
        {
            throw new GameToolException(reason);
        }
    }

    private static void RequireClosed(Func<string?> ensureClosed)
    {
        if (ensureClosed() is { } running)
        {
            throw new GameToolException(running);
        }
    }

    /// <summary>Hashes a file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The lowercase SHA-256.</returns>
    internal static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
