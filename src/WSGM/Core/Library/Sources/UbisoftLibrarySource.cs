using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Finds the games Ubisoft Connect has installed.</summary>
/// <remarks>
///     <para>
///         Follows Playnite's Ubisoft library for what is installed: one key per game under
///         <c>HKLM\SOFTWARE\ubisoft\Launcher\Installs</c>, holding its install folder. Names, executables
///         and working folders come from the launcher's product cache, a protobuf file of YAML documents
///         that Playnite and Steam ROM Manager both read. A game the cache does not know is still
///         listed, under its folder's name, with only the launcher route.
///     </para>
///     <para>
///         The cache is also what tells a game from an add-on or from a game another store sells and
///         only borrows Connect for, both of which register an install too. When there are installs
///         and the cache cannot be read, the scan of this source fails rather than offering every
///         install as a game: Connect writes the cache the first time it starts.
///     </para>
///     <para>
///         The direct route comes first when the cache names an executable that exists. Ubisoft games
///         open Connect themselves when they need it, so starting the executable keeps Steam's overlay
///         on the game.
///     </para>
/// </remarks>
public sealed class UbisoftLibrarySource : ILibrarySource
{
    private const string LauncherName = "Ubisoft Connect";

    private const string DirectEvidence =
        ShortcutRoute.DirectEvidence + " The game opens Ubisoft Connect itself when it needs it.";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<UbisoftInstall>> _installs;
    private readonly string _localAppData;
    private readonly Func<string, byte[]?> _readBytes;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;

    /// <summary>Creates the source over this machine's registry and files.</summary>
    public UbisoftLibrarySource()
        : this(
            ReadInstalls,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LibraryFiles.ReadBytes,
            File.Exists,
            Directory.Exists,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="installs">Lists the launcher's registered installs.</param>
    /// <param name="localAppData">The local application data folder the product cache lives under.</param>
    /// <param name="readBytes">Reads a file's bytes, or returns null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="resolveProtocol">Resolves the program a URI opens with, or null.</param>
    internal UbisoftLibrarySource(
        Func<IReadOnlyList<UbisoftInstall>> installs,
        string localAppData,
        Func<string, byte[]?> readBytes,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(readBytes);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _installs = installs;
        _localAppData = localAppData;
        _readBytes = readBytes;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _resolveProtocol = resolveProtocol;
    }

    /// <inheritdoc />
    public string Id => "ubisoft";

    /// <inheritdoc />
    public string DisplayName => LauncherName;

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return LauncherFolder(programs) is null
            ? SourceAvailability.NotFound
            : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> Discover(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        if (LauncherFolder(programs) is not { } launcher)
        {
            return [];
        }

        var installs = _installs();
        if (installs.Count == 0)
        {
            return [];
        }

        var products = ReadProducts(launcher)
                       ?? throw new InvalidDataException(
                           "Ubisoft Connect's product cache could not be read, so its games cannot be told from "
                           + "add-ons and from games other stores sell. Start Ubisoft Connect once, then scan again.");
        Dictionary<uint, UbisoftProduct> byId = [];
        HashSet<uint> addons = [];
        foreach (var product in products)
        {
            byId.TryAdd(product.UplayId, product);
            addons.UnionWith(product.Addons);
        }

        foreach (var product in products)
        {
            // Some installs are keyed by the install id rather than the Ubisoft id.
            byId.TryAdd(product.InstallId, product);
        }

        List<DiscoveredGame> games = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var install in installs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = LibraryFiles.InstallFolder(install.InstallDir);
            if (install.Id.Length == 0 || location.Length == 0 || !seen.Add(install.Id)
                || !_directoryExists(location))
            {
                continue;
            }

            UbisoftProduct? product = null;
            if (uint.TryParse(install.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                byId.TryGetValue(id, out product);
                if (product is not null && (product.ThirdParty || product.IsUlc || !product.HasStartGame
                                            || addons.Contains(id)))
                {
                    continue;
                }
            }

            List<ShortcutRoute> routes = [];
            if (product is { Executable.Length: > 0 })
            {
                var executable = LibraryFiles.Under(location, product.Executable);
                if (_fileExists(executable))
                {
                    // The cache names the working folder relative to the install; without one the game
                    // starts where its executable is.
                    var directory = product.WorkingDirectory is { } working
                        ? LibraryFiles.InstallFolder(LibraryFiles.Under(location, working))
                        : Path.GetDirectoryName(executable) ?? location;
                    routes.Add(new ShortcutRoute(
                        "direct", "Game executable", executable, directory, string.Empty, DirectEvidence));
                }
            }

            if (_resolveProtocol($"uplay://launch/{install.Id}/0") is { } command)
            {
                routes.Add(ShortcutRoute.ThroughLauncher(
                    command, LauncherName, ShortcutRoute.FollowedLauncherEvidence(LauncherName), location));
            }

            if (routes.Count > 0)
            {
                games.Add(DiscoveredGame.Command(this,
                    install.Id,
                    product is { Name.Length: > 0 } ? product.Name : Path.GetFileName(location),
                    location,
                    routes));
            }
        }

        return games;
    }

    /// <summary>The product cache's entries, or null when no cache could be read and parsed.</summary>
    private IReadOnlyList<UbisoftProduct>? ReadProducts(string launcher)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_localAppData, "Ubisoft Game Launcher", "cache", "configuration",
                         "configurations"),
                     Path.Combine(launcher, "cache", "configuration", "configurations")
                 })
        {
            if (_readBytes(path) is { Length: > 0 } bytes
                && UbisoftConfigurations.Parse(bytes) is { Count: > 0 } products)
            {
                return products;
            }
        }

        return null;
    }

    private string? LauncherFolder(IReadOnlyList<UninstallEntry> programs)
    {
        var program = UninstallEntries.FindProgram(
            programs,
            entry => entry.DisplayName is "Ubisoft Connect" or "Uplay",
            _fileExists,
            "UbisoftConnect.exe",
            "upc.exe");
        return program is null ? null : Path.GetDirectoryName(program);
    }

    private static IReadOnlyList<UbisoftInstall> ReadInstalls()
    {
        return UninstallEntries.SubKeys(
            [(RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64)],
            @"SOFTWARE\ubisoft\Launcher\Installs",
            (game, name) => game.GetValue("InstallDir") is string { Length: > 0 } directory
                ? new UbisoftInstall(name, directory)
                : null);
    }
}
