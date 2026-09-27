using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One installed copy of an itch game, as butler's database records it.</summary>
/// <param name="GameId">The game's itch id.</param>
/// <param name="Title">The game's title.</param>
/// <param name="Classification">What itch classifies it as, such as <c>game</c> or <c>tool</c>, or empty.</param>
/// <param name="Verdict">Butler's JSON verdict on what in the install can be launched, or empty.</param>
/// <param name="InstallFolder">The install folder the cave names, or empty.</param>
public sealed record ItchCave(
    string GameId,
    string Title,
    string Classification,
    string Verdict,
    string InstallFolder);

/// <summary>Finds the games the itch app has installed on this machine.</summary>
/// <remarks>
///     <para>
///         The itch app keeps its installs, which it calls caves, in butler's SQLite database. Each
///         cave carries a verdict, butler's own scan of the install for launchable files, and the
///         first Windows executable in it is what the game runs, as Steam ROM Manager reads it.
///     </para>
///     <para>
///         As in Playnite, only games and tools are offered; itch also sells soundtracks, books and
///         assets. A game installed more than once is offered once.
///     </para>
/// </remarks>
public sealed class ItchLibrarySource : ILibrarySource
{
    /// <summary>The query this source expects butler's schema to answer.</summary>
    /// <remarks>
    ///     Columns as butler's models name them through its ORM (<c>database/models/cave.go</c>,
    ///     <c>install_location.go</c>): a cave's folder is its custom install folder when it has one,
    ///     otherwise its install location's path joined with its install folder name, exactly as
    ///     <c>Cave.GetInstallFolder</c> resolves it.
    /// </remarks>
    internal const string CavesQuery =
        "select g.id, g.title, g.classification, c.verdict, l.path, c.install_folder_name, "
        + "c.custom_install_folder "
        + "from caves c join games g on c.game_id = g.id "
        + "left join install_locations l on c.install_location_id = l.id";

    /// <summary>The same without the classification, in case a butler release moved it.</summary>
    internal const string UnclassifiedCavesQuery =
        "select g.id, g.title, '', c.verdict, l.path, c.install_folder_name, c.custom_install_folder "
        + "from caves c join games g on c.game_id = g.id "
        + "left join install_locations l on c.install_location_id = l.id";

    /// <summary>Steam ROM Manager's query, used when the fuller ones find nothing.</summary>
    internal const string MinimalCavesQuery =
        "select g.id, g.title, c.verdict from caves c join games g on c.game_id = g.id";

    private const string DirectLabel = "Game executable";

    private const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it.";

    private readonly string _databasePath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<ItchCave>> _readCaves;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's itch install.</summary>
    public ItchLibrarySource()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "db", "butler.db"),
            ReadCaves,
            File.Exists,
            UninstallEntries.Read)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="databasePath">Where butler's database is.</param>
    /// <param name="readCaves">Reads the caves from a database file.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="uninstall">Lists Windows' uninstall entries.</param>
    public ItchLibrarySource(
        string databasePath,
        Func<string, IReadOnlyList<ItchCave>> readCaves,
        Func<string, bool> fileExists,
        Func<IReadOnlyList<UninstallEntry>> uninstall)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        ArgumentNullException.ThrowIfNull(readCaves);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(uninstall);
        _databasePath = databasePath;
        _readCaves = readCaves;
        _fileExists = fileExists;
        _uninstall = uninstall;
    }

    /// <inheritdoc />
    public string Id => "itch";

    /// <inheritdoc />
    public string DisplayName => "itch";

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return _fileExists(_databasePath)
               || _uninstall().Any(entry => string.Equals(entry.DisplayName, "itch", StringComparison.Ordinal))
            ? new SourceAvailability(true, "Installed")
            : SourceAvailability.NotFound;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() => Discover(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the caves from butler's database.</summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>The caves, empty when the database cannot be read.</returns>
    internal static IReadOnlyList<ItchCave> ReadCaves(string databasePath)
    {
        foreach (var query in new[] { CavesQuery, UnclassifiedCavesQuery })
        {
            var caves = LauncherDatabase.ReadRows(databasePath, query, reader => new ItchCave(
                LauncherDatabase.Text(reader, 0),
                LauncherDatabase.Text(reader, 1),
                LauncherDatabase.Text(reader, 2),
                LauncherDatabase.Text(reader, 3),
                Folder(LauncherDatabase.Text(reader, 6), LauncherDatabase.Text(reader, 4),
                    LauncherDatabase.Text(reader, 5))));
            if (caves.Count > 0)
            {
                return caves;
            }
        }

        // Only what Steam ROM Manager relies on, in case a butler release renamed the rest.
        return LauncherDatabase.ReadRows(databasePath, MinimalCavesQuery, reader => new ItchCave(
            LauncherDatabase.Text(reader, 0),
            LauncherDatabase.Text(reader, 1),
            string.Empty,
            LauncherDatabase.Text(reader, 2),
            string.Empty));
    }

    /// <summary>Picks the executable a verdict says the install runs.</summary>
    /// <param name="verdict">Butler's verdict JSON.</param>
    /// <param name="fallbackFolder">The install folder, used when the verdict names no base path.</param>
    /// <returns>The executable's full path and the folder it was resolved against, or null.</returns>
    internal static (string Executable, string BasePath)? Executable(string verdict, string fallbackFolder)
    {
        if (verdict.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(verdict);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            var basePath = root.TryGetProperty("basePath", out var baseElement)
                           && baseElement.ValueKind is JsonValueKind.String
                ? Normalize(baseElement.GetString() ?? string.Empty)
                : string.Empty;
            if (basePath.Length == 0)
            {
                basePath = Normalize(fallbackFolder);
            }

            if (basePath.Length == 0)
            {
                return null;
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind is not JsonValueKind.Object
                    || !candidate.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind is not JsonValueKind.String
                    || pathElement.GetString() is not { Length: > 0 } relative)
                {
                    continue;
                }

                var flavor = candidate.TryGetProperty("flavor", out var flavorElement)
                             && flavorElement.ValueKind is JsonValueKind.String
                    ? flavorElement.GetString()
                    : null;
                if (string.Equals(flavor, "windows", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return (Path.Combine(basePath, Normalize(relative)), basePath);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // A verdict that does not parse names nothing to run.
        }

        return null;
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        if (!_fileExists(_databasePath))
        {
            return [];
        }

        List<DiscoveredGame> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var cave in _readCaves(_databasePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cave.GameId.Length == 0 || !Wanted(cave.Classification) || seen.Contains(cave.GameId))
            {
                continue;
            }

            var resolved = Executable(cave.Verdict, cave.InstallFolder);
            if (resolved is null || !_fileExists(resolved.Value.Executable))
            {
                continue;
            }

            var (executable, basePath) = resolved.Value;
            seen.Add(cave.GameId);
            var route = new ShortcutRoute(
                "direct",
                DirectLabel,
                executable,
                Path.GetDirectoryName(executable) ?? basePath,
                string.Empty,
                DirectEvidence);
            found.Add(new DiscoveredGame(
                Id,
                cave.GameId,
                cave.Title.Length > 0 ? cave.Title : Path.GetFileNameWithoutExtension(executable),
                basePath,
                new GameLaunch(DirectLabel, true, DirectEvidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                [route]));
        }

        return found;
    }

    /// <summary>Whether a classification is one Playnite imports by default.</summary>
    /// <remarks>An unknown classification is kept: the minimal query cannot read it.</remarks>
    private static bool Wanted(string classification)
    {
        return classification.Length == 0
               || classification.Equals("game", StringComparison.OrdinalIgnoreCase)
               || classification.Equals("tool", StringComparison.OrdinalIgnoreCase);
    }

    private static string Folder(string custom, string location, string folderName)
    {
        if (custom.Length > 0)
        {
            return Normalize(custom);
        }

        return location.Length == 0 || folderName.Length == 0
            ? string.Empty
            : Path.Combine(Normalize(location), Normalize(folderName));
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\');
    }
}
