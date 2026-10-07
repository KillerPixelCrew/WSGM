using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>A configured ROM library; its identity is independent of the current mount path.</summary>
public sealed class RomSourceConfig
{
    /// <summary>The source identity, retained when its root moves or remounts.</summary>
    public string Id { get; set; } = "";

    /// <summary>The friendly library and storage label.</summary>
    public string Name { get; set; } = "";

    /// <summary>The configured directory bound to its expected volume.</summary>
    public ManagedContentPath Root { get; set; } = new() { Directory = true };

    /// <summary>The emulated system, independent of emulator choice.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The preferred installation for this source; empty follows the system preference.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The explicit core used by a RetroArch source.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>Whether discovery visits subdirectories.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Whether display names remove common bracketed filename annotations.</summary>
    public bool TitleCleanup { get; set; } = true;

    /// <summary>Accepted extensions; an empty list uses the system profile.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Filename or relative-path wildcard exclusions.</summary>
    public List<string> Exclusions { get; set; } = [];

    /// <summary>Typed launch argument override; empty follows the emulator definition.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Copies the source and its mutable matching rules.</summary>
    public RomSourceConfig Copy()
    {
        var copy = (RomSourceConfig)MemberwiseClone();
        copy.Root = Root.Copy();
        copy.Extensions = [.. Extensions];
        copy.Exclusions = [.. Exclusions];
        copy.Arguments = [.. Arguments];
        return copy;
    }

    /// <summary>Resolves the selected root on its expected volume's current mount.</summary>
    public string ResolveRoot()
    {
        return ManagedContentStorage.ResolvePath(Root);
    }
}

/// <summary>A user-defined command source with an explicit backing target.</summary>
public sealed class ManualShortcutConfig
{
    /// <summary>The durable user-authored source identity.</summary>
    public string Id { get; set; } = "";

    /// <summary>The title shown in Steam and import review.</summary>
    public string Name { get; set; } = "";

    /// <summary>The explicit executable path.</summary>
    public string Target { get; set; } = "";

    /// <summary>Verbatim arguments; an explicit content token may be resolved at launch.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>The authored working directory, or empty for the program directory.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>The distinct required backing file, or empty for the target itself.</summary>
    public string ContentPath { get; set; } = "";

    /// <summary>The friendly required library or storage label.</summary>
    public string Location { get; set; } = "";

    /// <summary>Whether this command participates in managed availability tracking.</summary>
    public bool LibraryBacked { get; set; } = true;

    /// <summary>The volume-bound file when this manual entry is a single ROM.</summary>
    public ManagedContentPath? RomPath { get; set; }

    /// <summary>The single ROM's canonical system, empty for an authored PC command.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The single ROM's selected installation, empty to follow the system preference.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The single ROM's explicit core selection.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>The single ROM's typed argument override.</summary>
    public List<string> RomArguments { get; set; } = [];

    /// <summary>The accepted suffix override retained for this explicit single ROM.</summary>
    public List<string> RomExtensions { get; set; } = [];

    /// <summary>Copies this source without retaining mutable configuration ownership.</summary>
    public ManualShortcutConfig Copy()
    {
        var copy = (ManualShortcutConfig)MemberwiseClone();
        copy.RomPath = RomPath?.Copy();
        copy.RomArguments = [.. RomArguments];
        copy.RomExtensions = [.. RomExtensions];
        return copy;
    }
}

/// <summary>The profiles the importer exposes instead of asking surfaces to invent command lines.</summary>
/// <param name="Id">The emulated system identity.</param>
/// <param name="Name">The user-visible system name.</param>
/// <param name="Extensions">The default accepted file extensions.</param>
/// <param name="ScreenscraperSystemId">The provider's system identity, or zero when unmapped.</param>
/// <param name="ParserKind">The matching behavior determined by this system.</param>
public sealed record RomSystemProfile(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    int ScreenscraperSystemId,
    RomParserKind ParserKind);

/// <summary>The actual entry-point parsing required by a system's supported launch formats.</summary>
public enum RomParserKind
{
    /// <summary>An individual ROM file.</summary>
    File,

    /// <summary>A disc image or descriptor with required companions.</summary>
    Disc,

    /// <summary>A verified PlayStation 3 extracted/installed EBOOT entry point.</summary>
    Eboot
}

/// <summary>The reviewed standalone profiles plus explicit installed-core system metadata.</summary>
public static class RomProfiles
{
    /// <summary>The reviewed first-release system profiles.</summary>
    public static IReadOnlyList<RomSystemProfile> All { get; } =
    [
        new("nes", "Nintendo Entertainment System", [".nes", ".fds", ".zip", ".7z"], 3,
            RomParserKind.File),
        new("snes", "Super Nintendo", [".sfc", ".smc", ".zip", ".7z"], 4, RomParserKind.File),
        new("gb", "Game Boy", [".gb", ".zip", ".7z"], 9, RomParserKind.File),
        new("gbc", "Game Boy Color", [".gbc", ".zip", ".7z"], 10, RomParserKind.File),
        new("gba", "Game Boy Advance", [".gba", ".zip", ".7z"], 12, RomParserKind.File),
        new("n64", "Nintendo 64", [".n64", ".z64", ".v64", ".zip"], 14, RomParserKind.File),
        new("nds", "Nintendo DS", [".nds", ".zip"], 15, RomParserKind.File),
        new("megadrive", "Mega Drive / Genesis", [".md", ".gen", ".bin", ".zip", ".7z"], 1,
            RomParserKind.File),
        new("mastersystem", "Sega Master System", [".sms", ".zip", ".7z"], 2, RomParserKind.File),
        new("gamegear", "Sega Game Gear", [".gg", ".zip", ".7z"], 21, RomParserKind.File),
        new("saturn", "Sega Saturn", [".cue", ".chd", ".m3u"], 22, RomParserKind.Disc),
        new("dreamcast", "Sega Dreamcast", [".gdi", ".cdi", ".chd", ".m3u"], 23, RomParserKind.Disc),
        new("psp", "PlayStation Portable", [".iso", ".cso", ".pbp"], 13, RomParserKind.File),
        new("psx", "PlayStation", [".cue", ".chd", ".pbp", ".m3u"], 57, RomParserKind.Disc),
        new("ps2", "PlayStation 2", [".iso", ".chd", ".cso", ".bin", ".img", ".mdf", ".gz"], 58,
            RomParserKind.Disc),
        new("ps3", "PlayStation 3", [".bin"], 59, RomParserKind.Eboot),
        new("gamecube", "Nintendo GameCube", [".iso", ".gcm", ".gcz", ".rvz", ".wia", ".ciso", ".dol", ".elf"],
            64, RomParserKind.File),
        new("wii", "Nintendo Wii", [".iso", ".wbfs", ".gcz", ".rvz", ".wia", ".ciso", ".wad", ".dol", ".elf"],
            16, RomParserKind.File),
        new("switch", "Nintendo Switch", [".xci", ".nsp", ".nca", ".nro", ".nso"], 225, RomParserKind.File)
    ];

    /// <summary>Finds a reviewed system profile by its identity.</summary>
    public static RomSystemProfile? Find(string id)
    {
        return All.FirstOrDefault(profile => profile.Id == EmulatorStorage.NormalizeSystemId(id));
    }

    /// <summary>Builds profiles from an already-loaded manager snapshot without file I/O.</summary>
    public static IReadOnlyList<RomSystemProfile> ForInstallations(IEnumerable<EmulatorInstallation> installations)
    {
        List<RomSystemProfile> profiles = [.. All];
        foreach (var core in installations.SelectMany(installation => installation.Cores))
        {
            foreach (var system in core.Systems)
            {
                var systemId = EmulatorStorage.NormalizeSystemId(system);
                var existing = profiles.FindIndex(profile =>
                    profile.Id.Equals(systemId, StringComparison.OrdinalIgnoreCase));
                var extensions =
                    core.Extensions.Select(extension => extension.StartsWith('.') ? extension : "." + extension);
                if (existing < 0)
                {
                    profiles.Add(new RomSystemProfile(systemId, system,
                        [.. extensions.Distinct(StringComparer.OrdinalIgnoreCase)],
                        0, RomParserKind.File));
                }
                else
                {
                    profiles[existing] = profiles[existing] with
                    {
                        Extensions =
                        [.. profiles[existing].Extensions.Concat(extensions).Distinct(StringComparer.OrdinalIgnoreCase)]
                    };
                }
            }
        }

        return profiles;
    }
}

/// <summary>Produces authoritative ROM paths and emulator selections for the existing importer.</summary>
/// <param name="source">The configured source and matching rules.</param>
/// <param name="profiles">The already-published system profiles, including known installed-core systems.</param>
public sealed class RomLibrarySource(RomSourceConfig source, IReadOnlyList<RomSystemProfile>? profiles = null)
    : ILibrarySource
{
    /// <inheritdoc />
    public string Id => source.Id;

    /// <inheritdoc />
    public LibrarySourceKind Kind => LibrarySourceKind.Rom;

    /// <inheritdoc />
    public string DisplayName => source.Name;

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        try
        {
            var root = source.ResolveRoot();
            return new SourceAvailability(Directory.Exists(root), source.Name);
        }
        catch (IOException)
        {
            return new SourceAvailability(false, "Storage unavailable");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var root = source.ResolveRoot();
        var systemId = EmulatorStorage.NormalizeSystemId(source.SystemId);
        var profile = (profiles ?? RomProfiles.All).FirstOrDefault(value => value.Id == systemId)
                      ?? throw new InvalidDataException("Choose a supported ROM system.");
        var parser = profile.ParserKind;
        var extensions = source.Extensions.Count > 0
            ? GameLibraryRules.NormalizeExtensions(source.Extensions)
            : profile.Extensions;
        var files = Walk(root, cancellationToken).ToArray();
        var parent = root;
        var binding = source.Root.Copy();
        binding.AbsolutePath = parent;
        binding.Directory = true;

        List<(FolderEntry File, string Key, List<string> Companions)> primaries = [];
        HashSet<string> companionFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var listed in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = listed.Path;
            var key = Path.GetRelativePath(parent, file);
            if (!extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                || source.Exclusions.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, key)
                                                    || FileSystemName.MatchesSimpleExpression(pattern,
                                                        Path.GetFileName(file))))
            {
                continue;
            }

            if (parser == RomParserKind.Eboot &&
                !Path.GetFileName(file).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> companions;
            try
            {
                companions = Companions(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"ROM descriptor {Path.GetFileName(file)} could not be read: {ex.Message}");
                continue;
            }

            companionFiles.UnionWith(companions);
            primaries.Add((listed, key, companions));
        }

        List<DiscoveredGame> games = [];
        foreach (var (listed, key, companions) in primaries.Where(item => !companionFiles.Contains(item.File.Path)))
        {
            var file = listed.Path;
            var rawName = parser == RomParserKind.Eboot ? EbootName(file) : Path.GetFileNameWithoutExtension(file);
            var name = source.TitleCleanup
                ? Regex.Replace(rawName, @"\s*[\[(][^\])]*[\])]", "", RegexOptions.CultureInvariant).Trim()
                : rawName;
            if (name.Length == 0)
            {
                name = rawName;
            }

            games.Add(CreateRom(Id, key, name, source.Name, listed, binding, companions, profile,
                source.EmulatorInstallationId, source.CoreId, source.Arguments));
        }

        return games;
    }

    internal static DiscoveredGame? ReadSingle(ManualShortcutConfig entry, IReadOnlyList<RomSystemProfile>? profiles)
    {
        var path = entry.RomPath!;
        var resolved = ManagedContentStorage.ResolvePath(path);
        var profile =
            (profiles ?? RomProfiles.All).FirstOrDefault(item =>
                item.Id == EmulatorStorage.NormalizeSystemId(entry.SystemId))
            ?? throw new InvalidDataException("Choose a supported ROM system.");
        var extensions = entry.RomExtensions.Count > 0 ? entry.RomExtensions : profile.Extensions;
        if (!extensions.Any(extension => resolved.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || (profile.ParserKind == RomParserKind.Eboot &&
                !Path.GetFileName(resolved).Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var root = path.Copy();
        root.AbsolutePath = Path.GetDirectoryName(resolved)!;
        root.RelativePath = Path.GetDirectoryName(root.RelativePath) ?? "";
        root.Directory = true;
        return CreateRom("manual", entry.Id, entry.Name, entry.Location.Length > 0 ? entry.Location : entry.Name,
            new FolderEntry(resolved, false, 0, new FileInfo(resolved).Length), root, Companions(resolved), profile,
            entry.EmulatorInstallationId, entry.CoreId, entry.RomArguments);
    }

    private static DiscoveredGame CreateRom(string sourceId, string key, string name, string location, FolderEntry file,
        ManagedContentPath root, IReadOnlyList<string> companions, RomSystemProfile profile, string installation,
        string core,
        IReadOnlyList<string> arguments)
    {
        var content = ManagedContentStorage.CreateRecord(sourceId, key, name, LibrarySourceKind.Rom, location,
            file.Path,
            binding: ManagedContentStorage.BindPath(root, file.Path));
        content.SystemId = profile.Id;
        content.EmulatorInstallationId = installation;
        content.CoreId = core;
        content.FollowSystemPreference = installation.Length == 0;
        content.Arguments = [.. arguments];
        content.SourceRoot = root.Copy();
        content.RequiredPaths = [.. companions.Select(path => ManagedContentStorage.BindPath(root, path))];
        ShortcutRoute route = new("managed", "Managed emulator", "", "", "",
            "Resolves the selected emulator and this ROM's storage when launched.", ManagedId: content.Id);
        return DiscoveredGame.Command(sourceId, key, name, file.Path, [route], content, true,
            new LibraryArtworkQuery("screenscraper", profile.ScreenscraperSystemId, Path.GetFileName(file.Path),
                file.Length));
    }

    private IEnumerable<FolderEntry> Walk(string root, CancellationToken token)
    {
        foreach (var item in LibraryFiles.List(root))
        {
            token.ThrowIfCancellationRequested();
            var attributes = item.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (source.IncludeSubfolders)
                {
                    foreach (var file in Walk(item.Path, token))
                    {
                        yield return file;
                    }
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    private static string EbootName(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetDirectoryName(path));
        if (directory is not null && Path.GetFileName(directory).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.GetFileName(directory) is { Length: > 0 } name ? name : "PlayStation 3 game";
    }

    private static List<string> Companions(string file, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(file))
        {
            return [];
        }

        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not ".cue" and not ".m3u" and not ".gdi")
        {
            return [];
        }

        List<string> result = [];
        foreach (var line in File.ReadLines(file))
        {
            string? name = null;
            if (extension == ".m3u")
            {
                var value = line.Trim();
                if (value.Length > 0 && !value.StartsWith('#'))
                {
                    name = value.Trim('"');
                }
            }
            else if (extension == ".cue")
            {
                var match = Regex.Match(line, "^\\s*FILE\\s+(?:\"([^\"]+)\"|(\\S+))",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                }
            }
            else
            {
                var match = Regex.Match(line, "^\\s*\\d+\\s+\\d+\\s+\\d+\\s+\\d+\\s+(?:\"([^\"]+)\"|(\\S+))",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                }
            }

            if (name is not null)
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, name));
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(path);
                    if (Path.GetExtension(path).ToLowerInvariant() is ".cue" or ".m3u" or ".gdi" && File.Exists(path))
                    {
                        result.AddRange(Companions(path, visited)
                            .Where(value => !result.Contains(value, StringComparer.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        return result;
    }
}

/// <summary>Offers one authored command through the existing preview and apply pipeline.</summary>
/// <param name="sources">The configured authored commands belonging to this one logical source.</param>
/// <param name="profiles">The parser systems available to authored ROM entries.</param>
public sealed class ManualLibrarySource(
    IReadOnlyList<ManualShortcutConfig> sources,
    IReadOnlyList<RomSystemProfile>? profiles = null) : ILibrarySource
{
    /// <inheritdoc />
    public string Id => "manual";

    /// <inheritdoc />
    public LibrarySourceKind Kind => LibrarySourceKind.Manual;

    /// <inheritdoc />
    public string DisplayName => "Manual shortcuts";

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return new SourceAvailability(true, "Manual shortcut");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DiscoveredGame> games = [];
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.RomPath is not null)
            {
                try
                {
                    if (RomLibrarySource.ReadSingle(source, profiles) is { } game)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.Warn($"Manual ROM {source.Name} could not be read: {ex.Message}");
                }

                continue;
            }

            ManagedContentRecord? content = null;
            if (source.LibraryBacked)
            {
                content = ManagedContentStorage.CreateRecord(Id, source.Id, source.Name, LibrarySourceKind.Manual,
                    source.Location.Length > 0 ? source.Location : source.Name,
                    source.ContentPath.Length > 0 ? source.ContentPath : source.Target);
                content.Program = ManagedContentStorage.CapturePath(source.Target);
                content.WorkingDirectory = ManagedContentStorage.CapturePath(source.WorkingDirectory.Length > 0
                    ? source.WorkingDirectory
                    : Path.GetDirectoryName(source.Target)!, true);
                content.RawArguments = source.Arguments;
            }

            ShortcutRoute route = new("direct", "Direct", source.Target, source.WorkingDirectory, source.Arguments,
                "Runs the command you defined.");
            games.Add(DiscoveredGame.Command(Id, source.Id, source.Name, source.ContentPath,
                [route], content, source.LibraryBacked, retainContent: source.LibraryBacked));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
    }
}
