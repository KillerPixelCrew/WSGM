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
///         Follows Playnite's Amazon library. The app lists its installs in a SQLite table, and each
///         game's <c>fuel.json</c> names the executable and arguments the app itself runs. A table that
///         cannot be read fails the scan of this source rather than listing nothing, so the games it
///         imported are not taken for uninstalled.
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

    private const string DirectLabel = "Game executable";

    private const string SignedDirectEvidence =
        ShortcutRoute.DirectEvidence + " The game asks Amazon Games for a sign-in and may not start without it.";

    private const string InstallsQuery =
        "select Id, ProductTitle, InstallDirectory from DbSet where Installed = 1 order by Id";

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

    /// <summary>Creates the source over this machine's Amazon Games install.</summary>
    public AmazonLibrarySource()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ReadInstalls,
            LibraryFiles.ReadText,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="localAppData">The user's local application data folder.</param>
    /// <param name="readInstalls">
    ///     Reads the installed games from the app's database file; throws when the file cannot be read.
    /// </param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program that opens a URI, or null.</param>
    internal AmazonLibrarySource(
        string localAppData,
        Func<string, IReadOnlyList<AmazonInstall>> readInstalls,
        Func<string, string?> readFile,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(readInstalls);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _localAppData = localAppData;
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
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return FindClient(programs) is null ? SourceAvailability.NotFound : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
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
            if (LibraryFiles.JsonProperty(document.RootElement, "Main", true) is not
                { ValueKind: JsonValueKind.Object } main)
            {
                return null;
            }

            List<string> arguments = [];
            if (LibraryFiles.JsonProperty(main, "Args", true) is { ValueKind: JsonValueKind.Array } args)
            {
                arguments.AddRange(args.EnumerateArray()
                    .Where(arg => arg.ValueKind is JsonValueKind.String)
                    .Select(arg => arg.GetString() ?? string.Empty)
                    .Where(arg => arg.Length > 0));
            }

            var scopes = LibraryFiles.JsonProperty(main, "AuthScopes", true) is
                             { ValueKind: JsonValueKind.Array } scopeArray
                         && scopeArray.GetArrayLength() > 0;
            return new AmazonFuel(
                LibraryFiles.JsonText(main, "Command", true),
                arguments,
                LibraryFiles.JsonText(main, "WorkingSubdirOverride", true),
                LibraryFiles.JsonText(main, "ClientId", true).Length > 0 && scopes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyList<DiscoveredGame> Discover(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        var database = DatabasePath;
        if (FindClient(programs) is null || !_fileExists(database))
        {
            return [];
        }

        List<DiscoveredGame> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var install in _readInstalls(database))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = LibraryFiles.InstallFolder(install.InstallDirectory);
            if (install.Id.Length == 0 || folder.Length == 0 || !_directoryExists(folder)
                || !seen.Add(install.Id))
            {
                continue;
            }

            var fuel = _readFile(Path.Combine(folder, "fuel.json")) is { } text ? ParseFuel(text) : null;
            var direct = Direct(folder, fuel);
            var launcher = Launcher(install.Id, folder);

            // A game that asks the app for a sign-in starts through the app by default.
            ShortcutRoute?[] ordered = fuel?.NeedsSignIn == true ? [launcher, direct] : [direct, launcher];
            List<ShortcutRoute> routes = [.. ordered.OfType<ShortcutRoute>()];
            if (routes.Count > 0)
            {
                found.Add(DiscoveredGame.Command(
                    Id,
                    install.Id,
                    install.ProductTitle.Length > 0 ? install.ProductTitle : Path.GetFileName(folder),
                    folder,
                    routes));
            }
        }

        return found;
    }

    private ShortcutRoute? Direct(string folder, AmazonFuel? fuel)
    {
        if (fuel is null || fuel.Command.Length == 0)
        {
            return null;
        }

        var executable = LibraryFiles.Under(folder, fuel.Command);
        if (!_fileExists(executable))
        {
            return null;
        }

        var workingFolder = fuel.WorkingSubdirectory.Length > 0
            ? LibraryFiles.Under(folder, fuel.WorkingSubdirectory)
            : folder;
        return new ShortcutRoute(
            "direct",
            DirectLabel,
            executable,
            workingFolder,
            LaunchArguments.Join(fuel.Arguments),
            fuel.NeedsSignIn ? SignedDirectEvidence : ShortcutRoute.DirectEvidence);
    }

    private ShortcutRoute? Launcher(string id, string installDirectory)
    {
        return _resolveProtocol($"amazon-games://play/{id}") is { } command
            ? ShortcutRoute.ThroughLauncher(
                command, LauncherLabel, ShortcutRoute.FollowedLauncherEvidence(LauncherLabel), installDirectory)
            : null;
    }

    /// <summary>The Amazon Games app's executable, or null when it is not installed.</summary>
    /// <param name="programs">Windows' installed-programs list.</param>
    private string? FindClient(IReadOnlyList<UninstallEntry> programs)
    {
        var registered = UninstallEntries.FindProgram(
            programs,
            entry => string.Equals(entry.DisplayName, "Amazon Games", StringComparison.Ordinal)
                     && entry.UninstallString.Contains(
                         "Uninstall Amazon Games.exe", StringComparison.OrdinalIgnoreCase),
            _fileExists,
            ClientExecutable);
        if (registered is not null)
        {
            return registered;
        }

        var fallback = Path.Combine(_localAppData, "Amazon Games", "App", ClientExecutable);
        return _fileExists(fallback) ? fallback : null;
    }

    private static IReadOnlyList<AmazonInstall> ReadInstalls(string databasePath)
    {
        return LauncherDatabase.ReadRows(databasePath, InstallsQuery, reader => new AmazonInstall(
            LauncherDatabase.Text(reader, 0),
            LauncherDatabase.Text(reader, 1),
            LauncherDatabase.Text(reader, 2)));
    }
}
