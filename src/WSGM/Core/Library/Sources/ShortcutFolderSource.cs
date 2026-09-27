using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>One entry of a folder listing.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Attributes">Its file attributes, for skipping hidden, system and linked entries.</param>
internal sealed record FolderEntry(string Path, bool IsDirectory, FileAttributes Attributes);

/// <summary>Offers the shortcuts and programs in one folder the user pointed the library at.</summary>
/// <remarks>
///     <para>
///         The folder source is for everything no launcher source covers: a Start menu folder, a
///         folder of desktop shortcuts, a portable game collection. A <c>.lnk</c> or <c>.exe</c>
///         becomes a direct route to its program. A <c>.url</c> with a launcher's own scheme runs
///         the program that scheme is registered to, as the shell would.
///     </para>
///     <para>
///         Entries that cannot become a shortcut are left out rather than offered unroutable:
///         anything Steam already runs, web links, shortcuts to documents or folders, and
///         installers, uninstallers and redistributables. The scan is bounded, and hidden, system
///         and linked entries are skipped so a junction cannot loop it.
///     </para>
/// </remarks>
public sealed class ShortcutFolderSource : ILibrarySource
{
    /// <summary>The most files one folder source offers.</summary>
    internal const int MaximumFiles = 2000;

    /// <summary>How many folders deep a recursive scan goes below the configured one.</summary>
    internal const int MaximumDepth = 8;

    /// <summary>File-name fragments that mark a program as a tool rather than a game.</summary>
    private static readonly string[] ToolNames =
        ["unins", "uninstall", "setup", "crashhandler", "vc_redist", "dxsetup"];

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly ShortcutFolderConfig _folder;
    private readonly Func<string, IReadOnlyList<FolderEntry>> _list;
    private readonly Func<string, ShellLinkInfo?> _readLink;
    private readonly Func<string, string?> _readText;
    private readonly Func<string, ProtocolCommand?> _resolveProtocol;

    /// <summary>Creates the source for one configured folder over the real file system.</summary>
    /// <param name="folder">The folder and what to read from it.</param>
    public ShortcutFolderSource(ShortcutFolderConfig folder)
        : this(folder, Directory.Exists, File.Exists, LibraryFiles.List, ShellLink.Read, LibraryFiles.ReadText,
            ProtocolHandler.Resolve)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="folder">The folder and what to read from it.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="list">Lists a folder's entries, empty when it cannot be read.</param>
    /// <param name="readLink">Reads a <c>.lnk</c> file, or returns null when it cannot be read.</param>
    /// <param name="readText">Reads a file's text, or returns null when it cannot be read.</param>
    /// <param name="resolveProtocol">Finds the program a URI scheme is registered to.</param>
    internal ShortcutFolderSource(
        ShortcutFolderConfig folder,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<FolderEntry>> list,
        Func<string, ShellLinkInfo?> readLink,
        Func<string, string?> readText,
        Func<string, ProtocolCommand?> resolveProtocol)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(readLink);
        ArgumentNullException.ThrowIfNull(readText);
        ArgumentNullException.ThrowIfNull(resolveProtocol);
        _folder = folder;
        _directoryExists = directoryExists;
        _fileExists = fileExists;
        _list = list;
        _readLink = readLink;
        _readText = readText;
        _resolveProtocol = resolveProtocol;
    }

    /// <inheritdoc />
    public string Id => _folder.Id;

    /// <inheritdoc />
    /// <remarks>The folder's own name, or the whole path for a drive root.</remarks>
    public string DisplayName
    {
        get
        {
            var name = Path.GetFileName(_folder.Path.TrimEnd('\\', '/'));
            return name.Length > 0 ? name : _folder.Path;
        }
    }

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return _folder.Path.Length > 0 && _directoryExists(_folder.Path)
            ? new SourceAvailability(true, _folder.Path)
            : new SourceAvailability(false, "Folder missing");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    /// <summary>Whether a file name marks an installer, uninstaller or redistributable.</summary>
    /// <param name="fileName">The file name, with or without its folder.</param>
    /// <returns>True when it should never be offered as a game.</returns>
    internal static bool IsTool(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return ToolNames.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        if (!Detect().Installed)
        {
            return [];
        }

        HashSet<string> extensions = new(
            _folder.Extensions.Select(extension => extension.StartsWith('.') ? extension : "." + extension),
            StringComparer.OrdinalIgnoreCase);
        List<string> files = [];
        Walk(_folder.Path, 0, extensions, files, cancellationToken);

        List<DiscoveredGame> found = [];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTool(file) || Route(file) is not { } route)
            {
                continue;
            }

            found.Add(new DiscoveredGame(
                Id,
                Path.GetRelativePath(_folder.Path, file),
                Path.GetFileNameWithoutExtension(file),
                route.Id == "direct" ? route.StartDirectory : file,
                new GameLaunch(route.Label, true, route.Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                [route]));
        }

        return found;
    }

    private void Walk(
        string directory, int depth, HashSet<string> extensions, List<string> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in _list(directory).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (files.Count >= MaximumFiles)
            {
                return;
            }

            // A reparse point can lead back into the tree or off to a network share.
            if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint))
                != 0)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                if (_folder.IncludeSubfolders && depth < MaximumDepth)
                {
                    Walk(entry.Path, depth + 1, extensions, files, cancellationToken);
                }
            }
            else if (extensions.Contains(Path.GetExtension(entry.Path)))
            {
                files.Add(entry.Path);
            }
        }
    }

    /// <summary>The route a file offers, or null when it cannot become a shortcut.</summary>
    private ShortcutRoute? Route(string file)
    {
        var extension = Path.GetExtension(file);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(file, string.Empty, string.Empty);
        }

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return _readLink(file) is { Target.Length: > 0 } link
                ? Direct(link.Target, link.Arguments, link.WorkingDirectory)
                : null;
        }

        return extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ? InternetShortcut(file) : null;
    }

    /// <summary>A route that runs a program directly, or null when it is not a usable program.</summary>
    private ShortcutRoute? Direct(string program, string arguments, string workingDirectory)
    {
        // Steam itself is never imported into Steam, whatever the shortcut asks it to run.
        if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || IsSteam(program)
            || IsTool(program)
            || !_fileExists(program))
        {
            return null;
        }

        return new ShortcutRoute(
            "direct",
            "Direct",
            program,
            workingDirectory.Length > 0 ? workingDirectory : Path.GetDirectoryName(program) ?? string.Empty,
            arguments.Trim(),
            "Steam starts the program the shortcut points at and tracks it directly.");
    }

    /// <summary>The route an Internet shortcut offers, or null when it has none.</summary>
    private ShortcutRoute? InternetShortcut(string file)
    {
        var url = _readText(file) is { } text
            ? LibraryFiles.IniValue(text, "InternetShortcut", "URL")?.Trim()
            : null;
        var separator = url?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (url is null || separator <= 0)
        {
            return null;
        }

        var scheme = url[..separator];
        if (scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
                ? Direct(uri.LocalPath, string.Empty, string.Empty)
                : null;
        }

        if (_resolveProtocol(url) is not { } command || IsSteam(command.Program) || !_fileExists(command.Program))
        {
            return null;
        }

        var program = Path.GetFileNameWithoutExtension(command.Program);
        return new ShortcutRoute(
            "launcher",
            $"Through {program}",
            command.Program,
            Path.GetDirectoryName(command.Program) ?? string.Empty,
            command.Arguments,
            $"Steam starts {program} with the shortcut's address, so it tracks {program} rather than the game.");
    }

    private static bool IsSteam(string program)
    {
        return Path.GetFileName(program).Equals("steam.exe", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The file reads the Game Library's folder-based sources share.</summary>
/// <remarks>Every read treats an unreadable file or folder as absent, as discovery evidence is.</remarks>
internal static class LibraryFiles
{
    /// <summary>Reads a file's text.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, or null when it cannot be read.</returns>
    internal static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lists a folder's subfolders.</summary>
    /// <param name="path">The folder.</param>
    /// <returns>Their full paths, empty when the folder cannot be read.</returns>
    internal static IReadOnlyList<string> Directories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Lists a folder's files and subfolders, with the attributes needed to skip some.</summary>
    /// <param name="path">The folder.</param>
    /// <returns>Its entries, empty when the folder cannot be read.</returns>
    internal static IReadOnlyList<FolderEntry> List(string path)
    {
        try
        {
            EnumerationOptions options = new()
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false
            };
            return new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", options)
                .Select(info => new FolderEntry(info.FullName, info is DirectoryInfo, info.Attributes))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Reads one value from an INI-style file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="section">The section, compared without regard to case.</param>
    /// <param name="key">The key, compared without regard to case.</param>
    /// <returns>The first matching value as written, or null.</returns>
    internal static string? IniValue(string text, string section, string key)
    {
        return Values(text, key)
            .Where(pair => pair.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    /// <summary>Reads one value from a file Qt or MultiMC's INI writer produced.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="key">The key.</param>
    /// <returns>The first value outside any section or in <c>[General]</c>, unquoted and unescaped, or null.</returns>
    /// <remarks>
    ///     Older MultiMC-family files have no section at all; Qt's writer puts the same keys under
    ///     <c>[General]</c>. Both escape backslashes, and Qt quotes a value with special characters.
    /// </remarks>
    internal static string? QtIniValue(string text, string key)
    {
        var raw = Values(text, key)
            .Where(pair => pair.Section.Length == 0
                           || pair.Section.Equals("General", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        return raw is null ? null : Unescape(raw);
    }

    private static IEnumerable<(string Section, string Value)> Values(string text, string key)
    {
        var section = string.Empty;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                yield return (section, line[(equals + 1)..].Trim());
            }
        }
    }

    private static string Unescape(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            value = value[1..^1];
        }

        StringBuilder result = new(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                result.Append(value[i]);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n':
                    result.Append('\n');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 'x':
                    // Qt 5 wrote non-ASCII characters as \x and up to four hex digits.
                    var digits = 0;
                    while (digits < 4 && i + 1 + digits < value.Length && Uri.IsHexDigit(value[i + 1 + digits]))
                    {
                        digits++;
                    }

                    if (digits == 0)
                    {
                        result.Append('x');
                        break;
                    }

                    result.Append((char)int.Parse(
                        value.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += digits;
                    break;
                default:
                    result.Append(next);
                    break;
            }
        }

        return result.ToString();
    }
}
