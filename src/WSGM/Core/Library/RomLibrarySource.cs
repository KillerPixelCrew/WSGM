using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

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
        var known = profiles ?? RomProfiles.All;
        if (systemId == "auto")
        {
            RomSystemProfile? Match(string path) => known.FirstOrDefault(profile =>
                profile.Id == EmulatorStorage.NormalizeSystemId(Path.GetFileName(path.TrimEnd('\\', '/'))));
            if (Match(root) is { } selected)
            {
                return Discover(root, selected, cancellationToken);
            }

            List<DiscoveredGame> collection = [];
            foreach (var directory in LibraryFiles.List(root).Where(item => item.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0
                    && Match(directory.Path) is { } system)
                {
                    collection.AddRange(Discover(directory.Path, system, cancellationToken));
                }
            }

            return collection;
        }

        var profile = known.FirstOrDefault(value => value.Id == systemId)
                      ?? throw new InvalidDataException("Choose a supported ROM system.");
        return Discover(root, profile, cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> Discover(string root, RomSystemProfile profile, CancellationToken cancellationToken)
    {
        var systemId = profile.Id;
        var parser = profile.ParserKind;
        var extensions = source.Extensions.Count > 0
            ? GameLibraryRules.NormalizeExtensions(source.Extensions)
            : profile.Extensions;
        var parent = source.ResolveRoot();
        var files = Walk(root, parent, parser, systemId == "switch", cancellationToken);
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
                || Excluded(key)
                || (systemId == "switch" && RomMetadata.IsSwitchAddOn(key)))
            {
                continue;
            }

            if ((parser is RomParserKind.Eboot or RomParserKind.InstalledEboot) &&
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
            var metadata = parser is RomParserKind.Eboot or RomParserKind.InstalledEboot
                ? RomMetadata.ReadSfo(file) : null;
            if (parser == RomParserKind.Eboot && metadata?.Category == "GD")
            {
                continue;
            }

            var rawName = metadata?.Title is { Length: > 0 } title ? title
                : parser == RomParserKind.Eboot ? EbootName(file)
                : parser == RomParserKind.InstalledEboot ? new DirectoryInfo(Path.GetDirectoryName(file)!).Name
                : Path.GetFileNameWithoutExtension(file);
            var name = source.TitleCleanup && metadata?.Title is not { Length: > 0 }
                ? RomMetadata.CleanTitle(rawName) : rawName;

            var game = CreateRom(Id, key, name, source.Name, listed, binding, companions, profile,
                source.EmulatorInstallationId, source.CoreId, source.Arguments);
            var rawFileName = Path.GetFileNameWithoutExtension(file);
            if (source.TitleCleanup && metadata?.Title is not { Length: > 0 } && rawFileName != name)
            {
                game = game with { Notes = [.. game.Notes, "File: " + Path.GetFileName(file)] };
            }

            if (metadata?.TitleId is { Length: > 0 } titleId)
            {
                game = game with { Notes = [.. game.Notes, "Title ID: " + titleId] };
            }

            games.Add(game);
        }

        if (source.TitleCleanup && parser is not (RomParserKind.Eboot or RomParserKind.InstalledEboot))
        {
            foreach (var group in games.Select((game, index) => (Game: game, Index: index))
                         .GroupBy(item => item.Game.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                foreach (var (game, index) in group)
                {
                    var name = Path.GetFileNameWithoutExtension(game.InstallPath) + " ["
                               + Path.GetExtension(game.InstallPath).TrimStart('.').ToUpperInvariant() + "]";
                    games[index] = game with { Name = name };
                    games[index].Content!.Name = name;
                }
            }
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
            || ((profile.ParserKind is RomParserKind.Eboot or RomParserKind.InstalledEboot) &&
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
            new LibraryArtworkQuery("screenscraper", profile.ScreenscraperSystemId,
                profile.ParserKind is RomParserKind.Eboot or RomParserKind.InstalledEboot ? name
                    : Path.GetFileName(file.Path),
                profile.ParserKind is RomParserKind.Eboot or RomParserKind.InstalledEboot ? 0 : file.Length)) with
        {
            Notes = profile.Id == "ps2" && Path.GetExtension(file.Path).Equals(".m3u", StringComparison.OrdinalIgnoreCase)
                ? ["Starts the first disc. Use the emulator's Change Disc menu to switch to another disc in this playlist."]
                : []
        };
    }

    private bool Excluded(string key)
    {
        key = key.Replace('\\', '/');
        return source.Exclusions.Any(value =>
        {
            var pattern = value.Replace('\\', '/');
            return FileSystemName.MatchesSimpleExpression(pattern, key)
                   || FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(key.TrimEnd('/')))
                   || (pattern.StartsWith("*/", StringComparison.Ordinal)
                       && FileSystemName.MatchesSimpleExpression(pattern.AsSpan(2), key));
        });
    }

    private IEnumerable<FolderEntry> Walk(string root, string sourceRoot, RomParserKind parser, bool switchContent,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parser == RomParserKind.Eboot)
        {
            var directory = new DirectoryInfo(Path.Combine(root, "USRDIR"));
            var eboot = new FileInfo(Path.Combine(directory.FullName, "EBOOT.BIN"));
            const FileAttributes ignored = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;
            if (directory.Exists && (directory.Attributes & ignored) == 0
                                 && eboot.Exists && (eboot.Attributes & ignored) == 0)
            {
                yield return new FolderEntry(eboot.FullName, false, eboot.Attributes, eboot.Length);
                yield break;
            }
        }

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
                if (source.IncludeSubfolders && !Excluded(Path.GetRelativePath(sourceRoot, item.Path) + "/")
                    && !(switchContent && RomMetadata.IsSwitchAddOn(Path.GetRelativePath(sourceRoot, item.Path) + "/_")))
                {
                    foreach (var file in Walk(item.Path, sourceRoot, parser, switchContent, token))
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
