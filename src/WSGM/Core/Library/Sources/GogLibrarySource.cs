using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Finds the GOG games installed on this machine, with or without GOG Galaxy.</summary>
/// <remarks>
///     <para>
///         Follows Playnite's GOG library. Every GOG installer, Galaxy's or the offline one, registers an
///         uninstall entry named <c>&lt;id&gt;_is1</c> and leaves a <c>goggame-&lt;id&gt;.info</c> file in the
///         install folder whose primary play task is the game's own executable. A title with no primary
///         task, or whose info names a different root game, is DLC and is skipped.
///     </para>
///     <para>
///         The direct route comes first because GOG games are DRM-free and start without the client.
///         The Galaxy route is offered beside it when Galaxy is installed, with the arguments Playnite
///         starts it with.
///     </para>
/// </remarks>
public sealed partial class GogLibrarySource : ILibrarySource
{
    private const string GalaxyExecutable = "GalaxyClient.exe";

    private const string GalaxyName = "GOG Galaxy";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _galaxyPath;
    private readonly Func<string, string?> _readFile;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public GogLibrarySource()
        : this(UninstallEntries.Read, ReadGalaxyPath, LibraryFiles.ReadText, File.Exists, Directory.Exists)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstall">Lists Windows' installed programs.</param>
    /// <param name="galaxyPath">Reads the folder Galaxy is installed in, or null when it is not.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    internal GogLibrarySource(
        Func<IReadOnlyList<UninstallEntry>> uninstall,
        Func<string?> galaxyPath,
        Func<string, string?> readFile,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(galaxyPath);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        _uninstall = uninstall;
        _galaxyPath = galaxyPath;
        _readFile = readFile;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
    }

    /// <inheritdoc />
    public string Id => "gog";

    /// <inheritdoc />
    public string DisplayName => GalaxyName;

    /// <inheritdoc />
    /// <remarks>GOG games run without Galaxy, so installed games alone make the source available.</remarks>
    public SourceAvailability Detect()
    {
        if (GalaxyFolder() is not null)
        {
            return new SourceAvailability(true, "Installed");
        }

        foreach (var entry in _uninstall())
        {
            if (GameId(entry) is not null)
            {
                return new SourceAvailability(true, "Games only");
            }
        }

        return SourceAvailability.NotFound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    /// <summary>The GOG product id an uninstall entry belongs to, or null when it is not a GOG game.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The id from the key name, when the publisher is GOG and it is not a bundle.</returns>
    internal static string? GameId(UninstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var match = GameKey().Match(entry.KeyName);
        if (!match.Success || entry.Publisher != "GOG.com"
                           || entry.KeyName.StartsWith("GOGPACK", StringComparison.Ordinal)
                           || entry.DisplayName.StartsWith("GOGPACK", StringComparison.Ordinal))
        {
            return null;
        }

        return match.Groups[1].Value;
    }

    /// <summary>Parses a <c>goggame-&lt;id&gt;.info</c> file.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The facts discovery needs, or null when the text is not a JSON object.</returns>
    internal static GogGameInfo? ParseInfo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hasPrimary = false;
            GogPlayTask? primary = null;
            if (LibraryFiles.JsonProperty(root, "playTasks") is { ValueKind: JsonValueKind.Array } tasks)
            {
                foreach (var task in tasks.EnumerateArray())
                {
                    if (LibraryFiles.JsonProperty(task, "isPrimary") is not { ValueKind: JsonValueKind.True })
                    {
                        continue;
                    }

                    hasPrimary = true;
                    var path = LibraryFiles.JsonText(task, "path");
                    if (primary is null && LibraryFiles.JsonText(task, "type") is "" or "FileTask" && path.Length > 0)
                    {
                        primary = new GogPlayTask(path, LibraryFiles.JsonText(task, "workingDir"),
                            LibraryFiles.JsonText(task, "arguments"));
                    }
                }
            }

            return new GogGameInfo(LibraryFiles.JsonText(root, "rootGameId"), hasPrimary, primary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Composes the direct route from a primary file task.</summary>
    /// <param name="task">The task.</param>
    /// <param name="location">The game's install folder.</param>
    /// <returns>The route, whether or not its executable exists.</returns>
    /// <remarks>
    ///     The path and the working directory are both relative to the install folder and are joined to
    ///     it as written. Some games, The Witcher 3 among them, repeat the working directory inside the
    ///     path; the path is still relative to the install folder, so it needs no correction.
    /// </remarks>
    internal static ShortcutRoute DirectRoute(GogPlayTask task, string location)
    {
        ArgumentNullException.ThrowIfNull(task);
        var executable = LibraryFiles.Under(location, task.Path);
        var directory = task.WorkingDir.Length > 0 ? LibraryFiles.Under(location, task.WorkingDir) : location;
        return new ShortcutRoute("direct", "Game executable", executable, directory, task.Arguments.Trim(),
            ShortcutRoute.DirectEvidence);
    }

    /// <summary>Galaxy's launch arguments for one game.</summary>
    /// <param name="id">The GOG product id.</param>
    /// <param name="location">The game's install folder.</param>
    /// <returns>The arguments Playnite starts Galaxy with, the folder quoted so a drive root survives.</returns>
    internal static string GalaxyArguments(string id, string location)
    {
        return $"/launchViaAutostart /gameId={id} /command=runGame {LaunchArguments.Named("/path=", location)}";
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var galaxy = GalaxyFolder();
        List<DiscoveredGame> games = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var entry in _uninstall())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GameId(entry) is not { } id || !seen.Add(id))
            {
                continue;
            }

            var location = LibraryFiles.InstallFolder(entry.InstallLocation);
            if (location.Length == 0 || !_directoryExists(location))
            {
                continue;
            }

            var info = ReadInfo(location, id);
            if (info is null || !info.HasPrimaryTask
                             || (info.RootGameId.Length > 0 && info.RootGameId != id))
            {
                // No primary play task, or another game as the root: DLC, as Playnite reads it.
                continue;
            }

            List<ShortcutRoute> routes = [];
            if (info.Primary is { } task)
            {
                var direct = DirectRoute(task, location);
                if (_fileExists(direct.Target))
                {
                    routes.Add(direct);
                }
            }

            if (galaxy is not null)
            {
                routes.Add(ShortcutRoute.ThroughLauncher(
                    Path.Combine(galaxy, GalaxyExecutable), GalaxyArguments(id, location), GalaxyName,
                    ShortcutRoute.FollowedLauncherEvidence(GalaxyName), location));
            }

            if (routes.Count > 0)
            {
                games.Add(DiscoveredGame.Command(Id, id, entry.DisplayName, location, routes));
            }
        }

        return games;
    }

    private GogGameInfo? ReadInfo(string location, string id)
    {
        var text = _readFile(Path.Combine(location, $"goggame-{id}.info"));
        return string.IsNullOrWhiteSpace(text) ? null : ParseInfo(text);
    }

    private string? GalaxyFolder()
    {
        var folder = _galaxyPath();
        return folder is { Length: > 0 } && _fileExists(Path.Combine(folder, GalaxyExecutable)) ? folder : null;
    }

    private static string? ReadGalaxyPath()
    {
        foreach (var path in new[]
                 {
                     @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", @"SOFTWARE\GOG.com\GalaxyClient\paths"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("client") is string { Length: > 0 } client)
                {
                    return client;
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                // An unreadable key is no Galaxy.
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+)_is1$", RegexOptions.CultureInvariant)]
    private static partial Regex GameKey();
}

/// <summary>What a <c>goggame-&lt;id&gt;.info</c> file says about launching its game.</summary>
/// <param name="RootGameId">The game this one belongs to, or empty.</param>
/// <param name="HasPrimaryTask">Whether any play task is marked primary. None means DLC.</param>
/// <param name="Primary">The first primary task that runs a file, or null.</param>
internal sealed record GogGameInfo(string RootGameId, bool HasPrimaryTask, GogPlayTask? Primary);

/// <summary>A GOG play task that runs a file.</summary>
/// <param name="Path">The executable, relative to the install folder.</param>
/// <param name="WorkingDir">The working directory, relative to the install folder, or empty.</param>
/// <param name="Arguments">Its arguments, or empty.</param>
internal sealed record GogPlayTask(string Path, string WorkingDir, string Arguments);
