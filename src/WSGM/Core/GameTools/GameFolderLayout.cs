using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core.GameTools;

/// <summary>A file found by the bounded walk.</summary>
/// <param name="RelativePath">The forward-slash path relative to the walked root.</param>
/// <param name="FullPath">The absolute path.</param>
/// <param name="Size">The size in bytes.</param>
internal sealed record GameFolderFile(string RelativePath, string FullPath, long Size)
{
    /// <summary>The file name.</summary>
    internal string Name => Path.GetFileName(RelativePath);

    /// <summary>The forward-slash relative directory, empty at the root.</summary>
    internal string Directory => RelativePath.Contains('/') ? RelativePath[..RelativePath.LastIndexOf('/')] : "";
}

/// <summary>The executables, libraries and directory names of a game folder, collected in one bounded walk.</summary>
/// <remarks>
///     One walk serves the runtime resolver and the upscaler and anti-cheat scans. It skips reparse
///     points so a junction cannot lead it outside the game, ignores inaccessible directories, and stops at
///     a depth and count cap so a mistaken root such as a whole drive cannot stall the page.
/// </remarks>
internal sealed class GameFolderLayout
{
    /// <summary>The deepest directory level visited below the root.</summary>
    internal const int MaximumDepth = 6;

    private const int MaximumDirectories = 6000;
    private const int MaximumFiles = 40000;

    private GameFolderLayout(string root, List<GameFolderFile> executables, List<GameFolderFile> libraries,
        List<GameFolderFile> drivers, HashSet<string> directories, bool truncated)
    {
        Root = root;
        Executables = executables;
        Libraries = libraries;
        Drivers = drivers;
        Directories = directories;
        Truncated = truncated;
    }

    /// <summary>The walked root.</summary>
    internal string Root { get; }

    /// <summary>Every <c>*.exe</c>.</summary>
    internal IReadOnlyList<GameFolderFile> Executables { get; }

    /// <summary>Every <c>*.dll</c>.</summary>
    internal IReadOnlyList<GameFolderFile> Libraries { get; }

    /// <summary>Every <c>*.sys</c>.</summary>
    internal IReadOnlyList<GameFolderFile> Drivers { get; }

    /// <summary>Every visited directory's forward-slash relative path, case-insensitive.</summary>
    internal IReadOnlySet<string> Directories { get; }

    /// <summary>Whether a cap stopped the walk early.</summary>
    internal bool Truncated { get; }

    /// <summary>Whether a relative directory exists in the walk.</summary>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>True when visited.</returns>
    internal bool HasDirectory(string relativePath)
    {
        return Directories.Contains(relativePath);
    }

    /// <summary>Whether a relative file was found.</summary>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>True when present among executables or libraries.</returns>
    internal bool HasFile(string relativePath)
    {
        return Libraries.Any(file => file.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
               || Executables.Any(file => file.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Walks a game folder.</summary>
    /// <param name="root">The install root.</param>
    /// <returns>The layout.</returns>
    internal static GameFolderLayout Scan(string root)
    {
        List<GameFolderFile> executables = [];
        List<GameFolderFile> libraries = [];
        List<GameFolderFile> drivers = [];
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var files = 0;
        Queue<(DirectoryInfo Directory, string Relative, int Depth)> pending = new();
        pending.Enqueue((new DirectoryInfo(root), "", 0));
        while (pending.Count > 0)
        {
            var (directory, relative, depth) = pending.Dequeue();
            if (directories.Count >= MaximumDirectories || files >= MaximumFiles)
            {
                truncated = true;
                break;
            }

            directories.Add(relative);
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (entry is DirectoryInfo child)
                {
                    if (depth < MaximumDepth)
                    {
                        pending.Enqueue((child, path, depth + 1));
                    }

                    continue;
                }

                files++;
                var extension = Path.GetExtension(entry.Name);
                var list = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ? executables
                    : extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ? libraries
                    : extension.Equals(".sys", StringComparison.OrdinalIgnoreCase) ? drivers
                    : null;
                list?.Add(new GameFolderFile(path, entry.FullName, ((FileInfo)entry).Length));
            }
        }

        return new GameFolderLayout(root, executables, libraries, drivers, directories, truncated || pending.Count > 0);
    }
}

/// <summary>Rejects folders too broad to be a game's program folder.</summary>
internal static class GameFolderGuard
{
    /// <summary>Explains why a picked executable's folder can't be a game folder, or returns null.</summary>
    /// <param name="executable">The absolute path the user picked.</param>
    /// <returns>A user-facing reason, or null when the folder is acceptable.</returns>
    /// <remarks>
    ///     The match is exact: subfolders of these locations are allowed, so a game under Program Files
    ///     or Documents still works. WindowsApps is refused because packaged games can't be modified.
    /// </remarks>
    internal static string? Reject(string executable)
    {
        string folder;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable)) ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Choose the game's program.";
        }

        if (folder.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            return "Games in the protected WindowsApps folder can't be modified.";
        }

        if (folder.Length == 0 || Path.GetPathRoot(folder) is { } root
            && Path.TrimEndingDirectorySeparator(root).Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            return "That folder is too broad. Choose a program inside the game's own folder.";
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), profile,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            profile.Length > 0 ? Path.Combine(profile, "Downloads") : "",
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? ""
        ];
        return broad.Any(path => path.Length > 0
                                 && Path.TrimEndingDirectorySeparator(path).Equals(folder, StringComparison.OrdinalIgnoreCase))
            ? "That folder is too broad. Choose a program inside the game's own folder."
            : null;
    }
}
