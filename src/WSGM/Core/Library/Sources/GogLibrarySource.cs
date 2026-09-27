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
///         Mirrors Playnite's GOG library. Every GOG installer, Galaxy's or the offline one, registers an
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

    private const string LauncherEvidence =
        "Starts through GOG Galaxy. WSGM follows the game, so Steam shows it running and keeps its "
        + "controller layout for as long as it runs; Steam's overlay may not reach it.";

    private const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it.";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _galaxyPath;
    private readonly Func<string, string?> _readFile;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public GogLibrarySource()
        : this(UninstallEntries.Read, ReadGalaxyPath, ReadText, File.Exists, Directory.Exists)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstall">Lists Windows' installed programs.</param>
    /// <param name="galaxyPath">Reads the folder Galaxy is installed in, or null when it is not.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    public GogLibrarySource(
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
    public string DisplayName => "GOG Galaxy";

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

            // No trailing separator: it would escape the closing quote of Galaxy's /path argument.
            var location = entry.InstallLocation.Replace('/', '\\').TrimEnd('\\');
            if (!_directoryExists(location))
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
                routes.Add(new ShortcutRoute(
                    "launcher", "GOG Galaxy", Path.Combine(galaxy, GalaxyExecutable), galaxy,
                    $"/launchViaAutostart /gameId={id} /command=runGame /path=\"{location}\"", LauncherEvidence,
                    location));
            }

            if (routes.Count == 0)
            {
                continue;
            }

            games.Add(new DiscoveredGame(
                Id,
                id,
                entry.DisplayName,
                location,
                new GameLaunch(routes[0].Label, true, routes[0].Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                routes));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
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
            if (root.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind != JsonValueKind.Object
                        || !task.TryGetProperty("isPrimary", out var isPrimary)
                        || isPrimary.ValueKind != JsonValueKind.True)
                    {
                        continue;
                    }

                    hasPrimary = true;
                    if (primary is null && Text(task, "type") is "" or "FileTask" && Text(task, "path").Length > 0)
                    {
                        primary = new GogPlayTask(Text(task, "path"), Text(task, "workingDir"),
                            Text(task, "arguments"));
                    }
                }
            }

            return new GogGameInfo(Text(root, "rootGameId"), hasPrimary, primary);
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
    ///     Paths in the info file are relative to the install folder. The working directory is too, and
    ///     some games, The Witcher 3 among them, repeat it inside the path; Playnite's correction for
    ///     that resolves to the same executable, so only the working directory needs it here.
    /// </remarks>
    internal static ShortcutRoute DirectRoute(GogPlayTask task, string location)
    {
        ArgumentNullException.ThrowIfNull(task);
        var executable = Path.Combine(location, task.Path.Replace('/', '\\').TrimStart('\\'));
        var directory = task.WorkingDir.Length > 0
            ? Path.Combine(location, task.WorkingDir.Replace('/', '\\').TrimStart('\\'))
            : location;
        return new ShortcutRoute("direct", "Game executable", executable, directory, task.Arguments.Trim(),
            DirectEvidence);
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

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
