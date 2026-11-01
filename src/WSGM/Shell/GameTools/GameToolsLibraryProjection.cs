using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Builds the Game Tools game list from the durable store and the cached release answers: what each game has
///     installed, its version, load name and status.
/// </summary>
/// <remarks>
///     Nothing is downloaded and no folder is walked. The status needs one existence check of the target folder and
///     the quick revalidation of the files the manifest owns, the same size and write-time check a page scan does;
///     an update is reported only when the channel's latest release is already cached.
/// </remarks>
internal static class GameToolsLibraryProjection
{
    /// <summary>Builds the list.</summary>
    /// <param name="store">The durable Game Tools state.</param>
    /// <param name="catalog">The tool catalog; manifests of tools it no longer has are left out.</param>
    /// <param name="latest">The cached latest release of a tool's channel, or null when none is known yet.</param>
    /// <param name="currentRoot">
    ///     The install folder a scan in this session found for a game, or null to use the root the manifest
    ///     recorded at its last write.
    /// </param>
    /// <returns>The list, games by name.</returns>
    internal static GameToolsLibrary Build(GameToolStore store, GameToolCatalog catalog,
        Func<GameToolDefinition, GameToolChannel, HostedRelease?> latest, Func<uint, string?> currentRoot)
    {
        List<GameToolsGameSummary> games = [];
        foreach (var record in store.Games)
        {
            List<GameToolsToolSummary> tools = [];
            foreach (var manifest in record.Tools)
            {
                if (catalog.Find(manifest.ToolId) is { } tool)
                {
                    tools.Add(Summarize(tool, manifest, latest, currentRoot(record.AppId) ?? manifest.InstallRoot));
                }
            }

            if (tools.Count == 0)
            {
                continue;
            }

            var name = record.Name.Length > 0 ? record.Name : record.AppId.ToString(CultureInfo.InvariantCulture);
            games.Add(new GameToolsGameSummary(record.AppId, name, record.IsShortcut,
                [.. tools.OrderBy(tool => Array.FindIndex(catalog.Tools, item => item.Id == tool.ToolId))]));
        }

        GameToolsGameSummary[] ordered =
            [.. games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(game => game.AppId)];
        return new GameToolsLibrary(false, null, ordered, ordered.Length,
            ordered.Count(game => game.Tools.Any(tool => tool.Status == "updateAvailable")));
    }

    /// <summary>One installed tool's summary.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="manifest">Its manifest.</param>
    /// <param name="latest">The cached latest release of a channel.</param>
    /// <param name="root">The game's install root.</param>
    /// <returns>The summary.</returns>
    internal static GameToolsToolSummary Summarize(GameToolDefinition tool, GameToolManifest manifest,
        Func<GameToolDefinition, GameToolChannel, HostedRelease?> latest, string root)
    {
        var channel = tool.ChannelOrDefault(manifest.Channel);
        var version = GameToolVersions.Installed(manifest);
        var label = channel.Display(version, manifest.AssetName);
        var named = tool.Name + " " + label;
        if (manifest.State != GameToolManifestState.Installed)
        {
            return Summary("interrupted", "Interrupted", named + " · an earlier change was interrupted", null, 0);
        }

        var directory = root.Length > 0 ? GameToolPaths.Resolve(root, manifest.TargetDirectory) : "";
        if (directory.Length == 0 || !Directory.Exists(directory))
        {
            return Summary("folderUnavailable", "Folder unavailable", named + " · its folder isn't available", null,
                0);
        }

        var (missing, modified) = GameToolInstaller.CheckOwned(directory, manifest);
        var changed = missing.Count + modified.Count;
        if (changed > 0)
        {
            return Summary("modified", "Needs repair", named + " · " + changed.ToString(CultureInfo.InvariantCulture)
                                                       + (changed == 1 ? " file" : " files")
                                                       + " changed outside WSGM", null, changed);
        }

        var release = latest(tool, channel);
        if (GameToolStatus.Relation(tool, manifest, release) == GameToolVersionRelation.UpdateAvailable)
        {
            var update = channel.BuildDate(release!.Asset.Name) is { } date
                ? "New build " + date.ToString("d MMM", CultureInfo.InvariantCulture)
                : "Update to " + release.Version;
            return Summary("updateAvailable", update, named + " as " + manifest.ProxyName, release.Version, 0);
        }

        return Summary("upToDate", "Up to date", named + " as " + manifest.ProxyName, null, 0);

        GameToolsToolSummary Summary(string status, string statusLabel, string detail, string? latestVersion,
            int changedFiles)
        {
            return new GameToolsToolSummary(tool.Id, tool.Name, version, label, channel.Id, channel.Label,
                channel == tool.DefaultChannel, manifest.ProxyName, status, statusLabel, detail, latestVersion,
                changedFiles);
        }
    }
}
