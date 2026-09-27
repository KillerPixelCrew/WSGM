using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds the games the Epic Games Launcher has installed.</summary>
/// <remarks>
///     <para>
///         Mirrors Playnite's Epic library: every installed title has a JSON manifest under
///         <c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests</c>, and add-ons, engine plugins and
///         Unreal Engine itself are dropped by the same rules Playnite applies. When a manifest's install
///         location no longer exists, the launcher's own <c>LauncherInstalled.dat</c> list is asked
///         instead, because it follows a game that was moved to another drive.
///     </para>
///     <para>
///         The launcher route opens the same <c>com.epicgames.launcher://</c> URI Playnite starts,
///         through whatever program the scheme is registered to.
///     </para>
/// </remarks>
public sealed class EpicLibrarySource : ILibrarySource
{
    private const string LauncherName = "Epic Games Launcher";

    private const string LauncherEvidence =
        "Starts through the Epic Games Launcher. WSGM follows the game, so Steam shows it running and keeps its "
        + "controller layout for as long as it runs; Steam's overlay may not reach it.";

    private const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it, "
        + "though some games refuse to start without the launcher.";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _listManifests;
    private readonly string _programData;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstall;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public EpicLibrarySource()
        : this(
            UninstallEntries.Read,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ListManifestFiles,
            ReadText,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstall">Lists Windows' installed programs.</param>
    /// <param name="programData">The ProgramData folder the launcher keeps its manifests under.</param>
    /// <param name="listManifests">Lists the <c>*.item</c> files in a folder, empty when it cannot.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program a URI opens with, or null.</param>
    public EpicLibrarySource(
        Func<IReadOnlyList<UninstallEntry>> uninstall,
        string programData,
        Func<string, IReadOnlyList<string>> listManifests,
        Func<string, string?> readFile,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(programData);
        ArgumentNullException.ThrowIfNull(listManifests);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _uninstall = uninstall;
        _programData = programData;
        _listManifests = listManifests;
        _readFile = readFile;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _resolveProtocol = resolveProtocol;
    }

    private string ManifestsPath => Path.Combine(_programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");

    private string InstalledListPath =>
        Path.Combine(_programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    /// <inheritdoc />
    public string Id => "epic";

    /// <inheritdoc />
    public string DisplayName => "Epic Games";

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return LauncherInstalled() ? new SourceAvailability(true, "Installed") : SourceAvailability.NotFound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!LauncherInstalled())
        {
            return Task.FromResult<IReadOnlyList<DiscoveredGame>>([]);
        }

        var installed = ReadInstalledList();
        Dictionary<string, DiscoveredGame> games = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _listManifests(ManifestsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = ReadManifest(file);
            if (manifest is null || manifest.AppName.Length == 0 || !IsGame(manifest))
            {
                continue;
            }

            var listedLocation = installed.GetValueOrDefault(manifest.AppName);
            var location = manifest.InstallLocation;
            if ((location.Length == 0 || !_directoryExists(location)) && listedLocation is { Length: > 0 })
            {
                location = listedLocation;
            }

            location = FixSeparators(location);
            if (location.Length == 0 || !_directoryExists(location))
            {
                continue;
            }

            var routes = Routes(manifest, location);
            if (routes.Count == 0)
            {
                continue;
            }

            var name = manifest.DisplayName.Length > 0
                ? manifest.DisplayName
                : Path.GetFileName(location.TrimEnd('\\'));
            DiscoveredGame game = new(
                Id,
                manifest.AppName,
                name,
                location,
                new GameLaunch(routes[0].Label, true, routes[0].Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                routes);

            // Some machines carry two manifests for one game from different locations. The installed
            // list is the one to believe, as Playnite does.
            if (!games.TryAdd(manifest.AppName, game)
                && listedLocation is { Length: > 0 }
                && string.Equals(FixSeparators(listedLocation), location, StringComparison.OrdinalIgnoreCase))
            {
                games[manifest.AppName] = game;
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games.Values.ToList());
    }

    /// <summary>Whether a manifest describes a game rather than an add-on or an engine component.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>False for DLC, Unreal Engine plugins and the engine itself.</returns>
    internal static bool IsGame(EpicManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.AppCategories.Contains("addons", StringComparer.Ordinal)
            && !manifest.AppCategories.Contains("addons/launchable", StringComparer.Ordinal))
        {
            return false;
        }

        return !manifest.AppCategories.Any(category => category is "plugins" or "plugins/engine")
               && !manifest.CompatibleApps.Any(app => app.StartsWith("UE_", StringComparison.Ordinal))
               && !manifest.TechnicalType.Contains("plugins/engine", StringComparison.Ordinal);
    }

    /// <summary>Parses one installed-game manifest.</summary>
    /// <param name="json">The <c>.item</c> file's text.</param>
    /// <returns>The manifest, or null when the text is not a JSON object.</returns>
    internal static EpicManifest? ParseManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new EpicManifest(
                Text(root, "AppName"),
                Text(root, "DisplayName"),
                Text(root, "InstallLocation"),
                Text(root, "LaunchExecutable"),
                Text(root, "LaunchCommand"),
                Text(root, "CatalogNamespace"),
                Text(root, "CatalogItemId"),
                Strings(root, "AppCategories"),
                Strings(root, "CompatibleApps"),
                Text(root, "TechnicalType"),
                Text(root, "MainGameAppName"));
        }
        catch (JsonException)
        {
            // A manifest hand-edited to move a game is often no longer valid JSON; it is skipped.
            return null;
        }
    }

    private List<ShortcutRoute> Routes(EpicManifest manifest, string location)
    {
        List<ShortcutRoute> routes = [];
        if (manifest.CatalogNamespace.Length > 0 && manifest.CatalogItemId.Length > 0)
        {
            var uri = $"com.epicgames.launcher://apps/{manifest.CatalogNamespace}%3A{manifest.CatalogItemId}"
                      + $"%3A{manifest.AppName}?action=launch&silent=true";
            if (_resolveProtocol(uri) is { } command)
            {
                routes.Add(new ShortcutRoute(
                    "launcher", LauncherName, command.Program, Path.GetDirectoryName(command.Program) ?? string.Empty,
                    command.Arguments, LauncherEvidence, location));
            }
        }

        if (manifest.LaunchExecutable.Length > 0)
        {
            var executable = Path.Combine(location, FixSeparators(manifest.LaunchExecutable).TrimStart('\\'));
            if (_fileExists(executable))
            {
                routes.Add(new ShortcutRoute(
                    "direct", "Game executable", executable, location, manifest.LaunchCommand.Trim(),
                    DirectEvidence));
            }
        }

        return routes;
    }

    private bool LauncherInstalled()
    {
        foreach (var entry in _uninstall())
        {
            if (entry.DisplayName != LauncherName || entry.InstallLocation.Length == 0)
            {
                continue;
            }

            if (_fileExists(LauncherExecutable(entry.InstallLocation, "Win32"))
                || _fileExists(LauncherExecutable(entry.InstallLocation, "Win64")))
            {
                return true;
            }
        }

        // The launcher's uninstall entry goes missing on some machines; its manifests folder does not.
        return _directoryExists(ManifestsPath);
    }

    private static string LauncherExecutable(string root, string platform)
    {
        return Path.Combine(root, "Launcher", "Portal", "Binaries", platform, "EpicGamesLauncher.exe");
    }

    private EpicManifest? ReadManifest(string file)
    {
        var text = _readFile(file);
        return text is null ? null : ParseManifest(text);
    }

    /// <summary>Reads the launcher's own list of install locations, keyed by app name.</summary>
    private Dictionary<string, string> ReadInstalledList()
    {
        Dictionary<string, string> locations = new(StringComparer.OrdinalIgnoreCase);
        var text = _readFile(InstalledListPath);
        if (text is null)
        {
            return locations;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("InstallationList", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return locations;
            }

            foreach (var app in list.EnumerateArray())
            {
                if (app.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = Text(app, "AppName");
                var location = Text(app, "InstallLocation");
                if (name.Length > 0 && location.Length > 0)
                {
                    locations.TryAdd(name, location);
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable list only removes the fallback.
        }

        return locations;
    }

    private static string FixSeparators(string path)
    {
        return path.Replace('/', '\\');
    }

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    private static IReadOnlyList<string> ListManifestFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.item") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
}

/// <summary>The parts of an Epic installed-game manifest discovery reads.</summary>
/// <param name="AppName">The launcher's stable id for the title.</param>
/// <param name="DisplayName">Its name, or empty.</param>
/// <param name="InstallLocation">Where it is installed, or empty.</param>
/// <param name="LaunchExecutable">Its executable, relative to the install location, or empty.</param>
/// <param name="LaunchCommand">The arguments the launcher passes it, or empty.</param>
/// <param name="CatalogNamespace">Its catalog namespace, part of the launch URI.</param>
/// <param name="CatalogItemId">Its catalog item id, part of the launch URI.</param>
/// <param name="AppCategories">Its categories, such as <c>games</c> or <c>addons</c>.</param>
/// <param name="CompatibleApps">The apps it plugs into, <c>UE_*</c> for engine plugins.</param>
/// <param name="TechnicalType">Its technical type, or empty.</param>
/// <param name="MainGameAppName">The game an add-on belongs to, or empty.</param>
internal sealed record EpicManifest(
    string AppName,
    string DisplayName,
    string InstallLocation,
    string LaunchExecutable,
    string LaunchCommand,
    string CatalogNamespace,
    string CatalogItemId,
    IReadOnlyList<string> AppCategories,
    IReadOnlyList<string> CompatibleApps,
    string TechnicalType,
    string MainGameAppName);
