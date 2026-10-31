using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

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

    private readonly HashSet<string> _files;

    private GameFolderLayout(string root, List<GameFolderFile> executables, List<GameFolderFile> libraries,
        List<GameFolderFile> drivers, HashSet<string> directories, bool truncated)
    {
        Root = root;
        Executables = executables;
        Libraries = libraries;
        Drivers = drivers;
        Directories = directories;
        Truncated = truncated;
        _files = new HashSet<string>(libraries.Concat(executables).Select(file => file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
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
        return _files.Contains(relativePath);
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
                list?.Add(new GameFolderFile(path, entry.FullName, ((FileInfo)entry).Length, entry.LastWriteTimeUtc));
            }
        }

        return new GameFolderLayout(root, executables, libraries, drivers, directories, truncated || pending.Count > 0);
    }
}
