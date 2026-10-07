using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>One entry of a folder listing.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Attributes">Its file attributes, for skipping hidden, system and linked entries.</param>
/// <param name="Length">The file length already available from directory enumeration.</param>
internal sealed record FolderEntry(string Path, bool IsDirectory, FileAttributes Attributes, long Length = 0);

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
///         anything Steam already runs, which is Steam itself, a <c>steam:</c> link and any program
///         inside a Steam library's <c>steamapps</c> folder; web links; shortcuts to documents or
///         folders; and installers, uninstallers and redistributables.
///     </para>
///     <para>
///         The scan offers everything the folder holds, as deep as it goes. Hidden, system and linked
///         entries are skipped so a junction cannot loop it.
///     </para>
/// </remarks>
public sealed class ShortcutFolderSource : ILibrarySource
{
    /// <summary>File-name fragments that mark a program as a tool rather than a game.</summary>
    /// <remarks><c>unins</c> covers <c>uninstall</c> and Inno Setup's <c>unins000</c> alike.</remarks>
    private static readonly string[] ToolNames = ["unins", "setup", "crashhandler", "vc_redist", "dxsetup"];

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
        _folder = folder.Copy();
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
    public LibrarySourceKind Kind => LibrarySourceKind.Folder;

    /// <inheritdoc />
    /// <remarks>The folder's own name, or the whole path for a drive root.</remarks>
    public string DisplayName
    {
        get
        {
            var name = Path.GetFileName(_folder.Root.AbsolutePath.TrimEnd('\\', '/'));
            return name.Length > 0 ? name : _folder.Root.AbsolutePath;
        }
    }

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return FolderExists(CurrentRoot())
            ? new SourceAvailability(true, _folder.Root.AbsolutePath)
            : new SourceAvailability(false, "Folder missing");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
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

    /// <summary>Whether Steam already runs a program: Steam itself, or anything in a Steam library.</summary>
    /// <param name="program">The program's full path.</param>
    /// <returns>True when importing it would put a second copy of a Steam game in the library.</returns>
    internal static bool IsSteam(string program)
    {
        return Path.GetFileName(program).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)
               || LibraryFiles.WindowsPath(program)
                   .Split('\\')
                   .Any(segment => segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
    }

    private bool FolderExists(string root)
    {
        return root.Length > 0 && _directoryExists(root);
    }

    private string CurrentRoot()
    {
        if (_folder.Root.VolumeId.Length == 0)
        {
            return _folder.Root.AbsolutePath;
        }

        try
        {
            return ManagedContentStorage.ResolvePath(_folder.Root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            return "";
        }
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var root = CurrentRoot();
        if (!FolderExists(root))
        {
            return [];
        }

        // ConfigStore keeps the list to the allowed extensions, each with its dot; a listed file's own
        // extension can be in any case.
        HashSet<string> extensions = new(_folder.Extensions, StringComparer.OrdinalIgnoreCase);
        List<string> files = [];
        Walk(root, extensions, files, cancellationToken);
        var binding = _folder.Root.Copy();
        binding.AbsolutePath = root;

        List<DiscoveredGame> found = [];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTool(file) || Route(file) is not { } route)
            {
                continue;
            }

            var key = Path.GetRelativePath(root, file);
            ManagedContentRecord? content = null;
            if (route.Id == "direct")
            {
                content = ManagedContentStorage.CreateRecord(Id, key, Path.GetFileNameWithoutExtension(file),
                    LibrarySourceKind.Folder, DisplayName, route.Target,
                    binding: ManagedContentStorage.BindPath(binding, route.Target));
                content.SourceRoot = binding.Copy();
                content.Program = content.BackingPath.Copy();
                content.WorkingDirectory = ManagedContentStorage.BindPath(binding, route.StartDirectory, true);
                content.RawArguments = route.LaunchOptions;
            }
            else
            {
                content = ManagedContentStorage.CreateRecord(Id, key, Path.GetFileNameWithoutExtension(file),
                    LibrarySourceKind.Folder, DisplayName, file,
                    binding: ManagedContentStorage.BindPath(binding, file));
                content.SourceRoot = binding.Copy();
                content.LaunchKind = ManagedLaunchKind.Native;
            }

            found.Add(DiscoveredGame.Command(this,
                key,
                Path.GetFileNameWithoutExtension(file),
                route.Id == "direct" ? route.StartDirectory : file,
                [route], content, content?.LaunchKind == ManagedLaunchKind.Direct,
                retainContent: true));
        }

        return found;
    }

    private void Walk(
        string directory, HashSet<string> extensions, List<string> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in _list(directory).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            // A reparse point can lead back into the tree or off to a network share.
            if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint))
                != 0)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                if (_folder.IncludeSubfolders)
                {
                    Walk(entry.Path, extensions, files, cancellationToken);
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
        // Nothing Steam already runs is imported into Steam, whatever the shortcut asks it to run.
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
        return ShortcutRoute.ThroughLauncher(
            command,
            $"Through {program}",
            $"Steam starts {program} with the shortcut's address, so it tracks {program} rather than the game.",
            string.Empty);
    }
}
