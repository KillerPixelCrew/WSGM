using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

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
///         The itch app keeps its installs, which it calls caves, in butler's SQLite database,
///         <c>%APPDATA%\itch\db\butler.db</c>. Each cave carries a verdict, butler's own scan of the
///         install for launchable files, and the first Windows executable in it is what the game runs,
///         as Steam ROM Manager reads it. The executable is resolved against the cave's install folder
///         as the database names it now, and against the folder the verdict recorded only when the
///         database names none: a verdict is taken when the game is installed and keeps the old folder
///         after the install is moved.
///     </para>
///     <para>
///         As in Playnite, only games and tools are offered; itch also sells soundtracks, books and
///         assets. A game installed more than once is offered once, the cave butler created first, so
///         the same scan always offers the same copy.
///     </para>
///     <para>
///         A database that cannot be read fails the scan of this source rather than listing nothing.
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
        + "left join install_locations l on c.install_location_id = l.id "
        + "order by g.id, c.id";

    /// <summary>The same without the classification, in case a butler release moved it.</summary>
    internal const string UnclassifiedCavesQuery =
        "select g.id, g.title, '', c.verdict, l.path, c.install_folder_name, c.custom_install_folder "
        + "from caves c join games g on c.game_id = g.id "
        + "left join install_locations l on c.install_location_id = l.id "
        + "order by g.id, c.id";

    /// <summary>Steam ROM Manager's query, used when the schema answers neither fuller one.</summary>
    internal const string MinimalCavesQuery =
        "select g.id, g.title, c.verdict from caves c join games g on c.game_id = g.id order by g.id, c.id";

    private const string DirectLabel = "Game executable";

    /// <summary>SQLite's generic error, which a query naming a column or table the schema lacks returns.</summary>
    private const int SqliteError = 1;

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
    /// <param name="readCaves">Reads the caves from a database file; throws when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="uninstall">Lists Windows' uninstall entries.</param>
    internal ItchLibrarySource(
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
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    /// <summary>Reads the caves from butler's database.</summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>The caves, empty when butler has none.</returns>
    /// <exception cref="LauncherDatabaseException">The database could not be read.</exception>
    /// <remarks>
    ///     One copy of the file, and the fuller queries tried in turn only when the schema cannot answer
    ///     them. A database that answers with no caves has none; it is not asked again.
    /// </remarks>
    internal static IReadOnlyList<ItchCave> ReadCaves(string databasePath)
    {
        return LauncherDatabase.Read(databasePath, connection =>
        {
            foreach (var query in new[] { CavesQuery, UnclassifiedCavesQuery })
            {
                try
                {
                    return LauncherDatabase.Query(connection, query, reader => new ItchCave(
                        LauncherDatabase.Text(reader, 0),
                        LauncherDatabase.Text(reader, 1),
                        LauncherDatabase.Text(reader, 2),
                        LauncherDatabase.Text(reader, 3),
                        Folder(LauncherDatabase.Text(reader, 6), LauncherDatabase.Text(reader, 4),
                            LauncherDatabase.Text(reader, 5))));
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError)
                {
                    // This butler release names things differently; try the next query.
                }
            }

            // Only what Steam ROM Manager relies on, in case a butler release renamed the rest.
            return LauncherDatabase.Query(connection, MinimalCavesQuery, reader => new ItchCave(
                LauncherDatabase.Text(reader, 0),
                LauncherDatabase.Text(reader, 1),
                string.Empty,
                LauncherDatabase.Text(reader, 2),
                string.Empty));
        });
    }

    /// <summary>Picks the executable a verdict says the install runs.</summary>
    /// <param name="verdict">Butler's verdict JSON.</param>
    /// <param name="installFolder">
    ///     The install folder the database names now, which wins over the one the verdict recorded.
    /// </param>
    /// <returns>The executable's full path and the folder it was resolved against, or null.</returns>
    internal static (string Executable, string BasePath)? Executable(string verdict, string installFolder)
    {
        if (verdict.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(verdict);
            var root = document.RootElement;
            if (LibraryFiles.JsonProperty(root, "candidates") is not { ValueKind: JsonValueKind.Array } candidates)
            {
                return null;
            }

            var basePath = LibraryFiles.InstallFolder(installFolder);
            if (basePath.Length == 0)
            {
                basePath = LibraryFiles.InstallFolder(LibraryFiles.JsonText(root, "basePath"));
            }

            if (basePath.Length == 0)
            {
                return null;
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                var relative = LibraryFiles.JsonText(candidate, "path");
                if (relative.Length == 0)
                {
                    continue;
                }

                if (string.Equals(LibraryFiles.JsonText(candidate, "flavor"), "windows",
                        StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return (LibraryFiles.Under(basePath, relative), basePath);
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

            if (Executable(cave.Verdict, cave.InstallFolder) is not var (executable, basePath)
                || !_fileExists(executable))
            {
                continue;
            }

            seen.Add(cave.GameId);
            found.Add(DiscoveredGame.Command(
                Id,
                cave.GameId,
                cave.Title.Length > 0 ? cave.Title : Path.GetFileNameWithoutExtension(executable),
                basePath,
                [
                    new ShortcutRoute(
                        "direct",
                        DirectLabel,
                        executable,
                        Path.GetDirectoryName(executable) ?? basePath,
                        string.Empty,
                        ShortcutRoute.DirectEvidence)
                ]));
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
            return LibraryFiles.WindowsPath(custom);
        }

        return location.Length == 0 || folderName.Length == 0
            ? string.Empty
            : Path.Combine(LibraryFiles.WindowsPath(location), LibraryFiles.WindowsPath(folderName));
    }
}
