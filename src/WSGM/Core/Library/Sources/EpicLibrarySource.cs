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
///         Follows Playnite's Epic library: every installed title has a JSON manifest under
///         <c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests</c>, and add-ons, engine plugins and
///         Unreal Engine itself are dropped by the same rules Playnite applies, plus an install still
///         downloading. When a manifest's install location no longer exists, the launcher's own
///         <c>LauncherInstalled.dat</c> list is asked instead, because it follows a game that was moved
///         to another drive.
///     </para>
///     <para>
///         The launcher route opens the same <c>com.epicgames.launcher://</c> URI Playnite starts,
///         through whatever program the scheme is registered to.
///     </para>
/// </remarks>
public sealed class EpicLibrarySource : ILibrarySource
{
    private const string LauncherName = "Epic Games Launcher";

    private const string DirectEvidence =
        ShortcutRoute.DirectEvidence + " Some games refuse to start without the launcher.";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _listManifests;
    private readonly string _programData;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public EpicLibrarySource()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            directory => LibraryFiles.Files(directory, "*.item"),
            LibraryFiles.ReadText,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="programData">The ProgramData folder the launcher keeps its manifests under.</param>
    /// <param name="listManifests">Lists the <c>*.item</c> files in a folder, empty when it cannot.</param>
    /// <param name="readFile">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program a URI opens with, or null.</param>
    internal EpicLibrarySource(
        string programData,
        Func<string, IReadOnlyList<string>> listManifests,
        Func<string, string?> readFile,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(programData);
        ArgumentNullException.ThrowIfNull(listManifests);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
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
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return LauncherInstalled(programs) ? new SourceAvailability(true, "Installed") : SourceAvailability.NotFound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
    }

    /// <summary>Whether a manifest describes a game rather than an add-on, an engine component or a download.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>False for DLC, Unreal Engine plugins, the engine itself and an incomplete install.</returns>
    /// <remarks>
    ///     An engine install is named <c>UE_</c> and its version, and does not always carry the engine
    ///     category, so its name decides as well. An install still downloading has no complete
    ///     executable to start.
    /// </remarks>
    internal static bool IsGame(EpicManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.IncompleteInstall || manifest.AppName.StartsWith("UE_", StringComparison.Ordinal))
        {
            return false;
        }

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
                LibraryFiles.JsonText(root, "AppName"),
                LibraryFiles.JsonText(root, "DisplayName"),
                LibraryFiles.JsonText(root, "InstallLocation"),
                LibraryFiles.JsonText(root, "LaunchExecutable"),
                LibraryFiles.JsonText(root, "LaunchCommand"),
                LibraryFiles.JsonText(root, "CatalogNamespace"),
                LibraryFiles.JsonText(root, "CatalogItemId"),
                LibraryFiles.JsonStrings(root, "AppCategories"),
                LibraryFiles.JsonStrings(root, "CompatibleApps"),
                LibraryFiles.JsonText(root, "TechnicalType"),
                root.TryGetProperty("bIsIncompleteInstall", out var incomplete)
                && incomplete.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            // A manifest hand-edited to move a game is often no longer valid JSON; it is skipped.
            return null;
        }
    }

    private IReadOnlyList<DiscoveredGame> Discover(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        if (!LauncherInstalled(programs))
        {
            return [];
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

            location = LibraryFiles.InstallFolder(location);
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
                : Path.GetFileName(location);
            var game = DiscoveredGame.Command(this, manifest.AppName, name, location, routes);

            // Some machines carry two manifests for one game from different locations. The installed
            // list is the one to believe, as Playnite does.
            if (!games.TryAdd(manifest.AppName, game)
                && listedLocation is { Length: > 0 }
                && string.Equals(LibraryFiles.InstallFolder(listedLocation), location,
                    StringComparison.OrdinalIgnoreCase))
            {
                games[manifest.AppName] = game;
            }
        }

        return games.Values.ToList();
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
                routes.Add(ShortcutRoute.ThroughLauncher(
                    command, LauncherName, ShortcutRoute.FollowedLauncherEvidence(LauncherName), location));
            }
        }

        if (manifest.LaunchExecutable.Length > 0)
        {
            var executable = LibraryFiles.Under(location, manifest.LaunchExecutable);
            if (_fileExists(executable))
            {
                routes.Add(new ShortcutRoute(
                    "direct", "Game executable", executable, location, manifest.LaunchCommand.Trim(),
                    DirectEvidence));
            }
        }

        return routes;
    }

    private bool LauncherInstalled(IReadOnlyList<UninstallEntry> programs)
    {
        var launcher = UninstallEntries.FindProgram(
            programs,
            entry => entry.DisplayName == LauncherName,
            _fileExists,
            Path.Combine("Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"),
            Path.Combine("Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"));

        // The launcher's uninstall entry goes missing on some machines; its manifests folder does not.
        return launcher is not null || _directoryExists(ManifestsPath);
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
            if (LibraryFiles.JsonProperty(document.RootElement, "InstallationList") is not
                { ValueKind: JsonValueKind.Array } list)
            {
                return locations;
            }

            foreach (var app in list.EnumerateArray())
            {
                var name = LibraryFiles.JsonText(app, "AppName");
                var location = LibraryFiles.JsonText(app, "InstallLocation");
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
/// <param name="IncompleteInstall">Whether the launcher is still installing it.</param>
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
    bool IncompleteInstall = false);
