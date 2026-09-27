using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One row of the Amazon Games app's install table.</summary>
/// <param name="Id">The game's Amazon id.</param>
/// <param name="ProductTitle">Its title.</param>
/// <param name="InstallDirectory">Where it is installed.</param>
public sealed record AmazonInstall(string Id, string ProductTitle, string InstallDirectory);

/// <summary>What a game's <c>fuel.json</c> says about starting it.</summary>
/// <param name="Command">The executable, relative to the install folder, or empty.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="WorkingSubdirectory">The working folder relative to the install folder, or empty.</param>
/// <param name="NeedsSignIn">Whether the game asks the Amazon Games app for a sign-in token.</param>
internal sealed record AmazonFuel(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingSubdirectory,
    bool NeedsSignIn);

/// <summary>Finds the games the Amazon Games app has installed on this machine.</summary>
/// <remarks>
///     <para>
///         Mirrors Playnite's Amazon library. The app lists its installs in a SQLite table, and each
///         game's <c>fuel.json</c> names the executable and arguments the app itself runs.
///     </para>
///     <para>
///         A game whose <c>fuel.json</c> names a client id and auth scopes asks the app for a sign-in
///         token when it starts, which is how Playnite decides the game needs the client. Such a game
///         goes through the app's <c>amazon-games://play/</c> URI by default; any other starts
///         directly.
///     </para>
/// </remarks>
public sealed class AmazonLibrarySource : ILibrarySource
{
    private const string ClientExecutable = "Amazon Games.exe";

    private const string LauncherLabel = "Amazon Games";

    private const string LauncherEvidence =
        "Starts through Amazon Games. WSGM follows the game, so Steam shows it running and keeps its "
        + "controller layout for as long as it runs; Steam's overlay may not reach it.";

    private const string DirectLabel = "Game executable";

    private const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it.";

    private const string SignedDirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it, but the game "
        + "asks Amazon Games for a sign-in and may not start without it.";

    private const string InstallsQuery =
        "select Id, ProductTitle, InstallDirectory from DbSet where Installed = 1";

    private static readonly JsonDocumentOptions FuelOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly string _localAppData;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, IReadOnlyList<AmazonInstall>> _readInstalls;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's Amazon Games install.</summary>
    public AmazonLibrarySource()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UninstallEntries.Read,
            ReadInstalls,
            ReadFile,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="localAppData">The user's local application data folder.</param>
    /// <param name="uninstall">Lists Windows' uninstall entries.</param>
    /// <param name="readInstalls">Reads the installed games from the app's database file.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program that opens a URI, or null.</param>
    public AmazonLibrarySource(
        string localAppData,
        Func<IReadOnlyList<UninstallEntry>> uninstall,
        Func<string, IReadOnlyList<AmazonInstall>> readInstalls,
        Func<string, string?> readFile,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(readInstalls);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _localAppData = localAppData;
        _uninstall = uninstall;
        _readInstalls = readInstalls;
        _readFile = readFile;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _resolveProtocol = resolveProtocol;
    }

    private string DatabasePath =>
        Path.Combine(_localAppData, "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite");

    /// <inheritdoc />
    public string Id => "amazon";

    /// <inheritdoc />
    public string DisplayName => "Amazon Games";

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return FindClient() is null ? SourceAvailability.NotFound : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() => Discover(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads what a game's <c>fuel.json</c> says about starting it.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The launch facts, or null when the file does not parse or has no <c>Main</c> section.</returns>
    /// <remarks>Property names are matched without regard to case, as Playnite's reader does.</remarks>
    internal static AmazonFuel? ParseFuel(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, FuelOptions);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || Property(document.RootElement, "Main") is not { ValueKind: JsonValueKind.Object } main)
            {
                return null;
            }

            List<string> arguments = [];
            if (Property(main, "Args") is { ValueKind: JsonValueKind.Array } args)
            {
                arguments.AddRange(args.EnumerateArray()
                    .Where(arg => arg.ValueKind is JsonValueKind.String)
                    .Select(arg => arg.GetString() ?? string.Empty)
                    .Where(arg => arg.Length > 0));
            }

            var scopes = Property(main, "AuthScopes") is { ValueKind: JsonValueKind.Array } scopeArray
                         && scopeArray.GetArrayLength() > 0;
            return new AmazonFuel(
                Text(main, "Command"),
                arguments,
                Text(main, "WorkingSubdirOverride"),
                Text(main, "ClientId").Length > 0 && scopes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Joins arguments the way Steam stores them, quoting any that contain a space.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The launch options.</returns>
    internal static string JoinArguments(IReadOnlyList<string> arguments)
    {
        return string.Join(' ', arguments.Select(argument =>
            argument.Contains(' ') && !argument.StartsWith('"') ? $"\"{argument}\"" : argument));
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var database = DatabasePath;
        if (FindClient() is null || !_fileExists(database))
        {
            return [];
        }

        List<DiscoveredGame> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var install in _readInstalls(database))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = install.InstallDirectory.Replace('/', '\\');
            if (install.Id.Length == 0 || folder.Length == 0 || !_directoryExists(folder)
                || !seen.Add(install.Id))
            {
                continue;
            }

            var fuel = ReadFuel(folder);
            var needsSignIn = fuel?.NeedsSignIn == true;
            var direct = Direct(folder, fuel);
            var launcher = Launcher(install.Id, folder);

            List<ShortcutRoute> routes = [];
            if (needsSignIn)
            {
                AddIfPresent(routes, launcher);
                AddIfPresent(routes, direct);
            }
            else
            {
                AddIfPresent(routes, direct);
                AddIfPresent(routes, launcher);
            }

            if (routes.Count == 0)
            {
                continue;
            }

            found.Add(new DiscoveredGame(
                Id,
                install.Id,
                install.ProductTitle.Length > 0 ? install.ProductTitle : Path.GetFileName(folder),
                folder,
                new GameLaunch(routes[0].Label, true, routes[0].Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                routes));
        }

        return found;
    }

    private ShortcutRoute? Direct(string folder, AmazonFuel? fuel)
    {
        if (fuel is null || fuel.Command.Length == 0)
        {
            return null;
        }

        var executable = Path.Combine(folder, fuel.Command.Replace('/', '\\'));
        if (!_fileExists(executable))
        {
            return null;
        }

        var workingFolder = fuel.WorkingSubdirectory.Length > 0
            ? Path.Combine(folder, fuel.WorkingSubdirectory.Replace('/', '\\'))
            : folder;
        return new ShortcutRoute(
            "direct",
            DirectLabel,
            executable,
            workingFolder,
            JoinArguments(fuel.Arguments),
            fuel.NeedsSignIn ? SignedDirectEvidence : DirectEvidence);
    }

    private ShortcutRoute? Launcher(string id, string installDirectory)
    {
        var command = _resolveProtocol($"amazon-games://play/{id}");
        return command is null
            ? null
            : new ShortcutRoute(
                "launcher",
                LauncherLabel,
                command.Program,
                Path.GetDirectoryName(command.Program) ?? string.Empty,
                command.Arguments,
                LauncherEvidence,
                installDirectory);
    }

    private AmazonFuel? ReadFuel(string folder)
    {
        try
        {
            var text = _readFile(Path.Combine(folder, "fuel.json"));
            return text is null ? null : ParseFuel(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable fuel.json leaves only the launcher route.
            return null;
        }
    }

    /// <summary>The Amazon Games app's executable, or null when it is not installed.</summary>
    private string? FindClient()
    {
        foreach (var entry in _uninstall())
        {
            if (entry.InstallLocation.Length == 0
                || !string.Equals(entry.DisplayName, "Amazon Games", StringComparison.Ordinal)
                || !entry.UninstallString.Contains("Uninstall Amazon Games.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var registered = Path.Combine(entry.InstallLocation, ClientExecutable);
            if (_fileExists(registered))
            {
                return registered;
            }
        }

        var fallback = Path.Combine(_localAppData, "Amazon Games", "App", ClientExecutable);
        return _fileExists(fallback) ? fallback : null;
    }

    private static void AddIfPresent(List<ShortcutRoute> routes, ShortcutRoute? route)
    {
        if (route is not null)
        {
            routes.Add(route);
        }
    }

    private static IReadOnlyList<AmazonInstall> ReadInstalls(string databasePath)
    {
        return LauncherDatabase.ReadRows(databasePath, InstallsQuery, reader => new AmazonInstall(
            LauncherDatabase.Text(reader, 0),
            LauncherDatabase.Text(reader, 1),
            LauncherDatabase.Text(reader, 2)));
    }

    private static string? ReadFile(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string Text(JsonElement element, string name)
    {
        return Property(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
