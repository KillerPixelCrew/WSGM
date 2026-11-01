using System.IO;

namespace WSGM.Core;

/// <summary>Converts the paths Game Tools records relative to a game's install root.</summary>
/// <remarks>
///     The durable record keeps paths relative to the install root so a game moved to another library still
///     resolves; a path the user picked outside the root is kept absolute.
/// </remarks>
internal static class GameToolPaths
{
    /// <summary>The absolute path of a recorded path.</summary>
    /// <param name="root">The game's current install root.</param>
    /// <param name="path">A forward-slash path relative to the root, or an absolute path.</param>
    /// <returns>The absolute path.</returns>
    internal static string Resolve(string root, string path)
    {
        return Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The forward-slash relative path of a file under a root, or its absolute path outside it.</summary>
    /// <param name="root">The install root.</param>
    /// <param name="path">The file.</param>
    /// <returns>The relative or absolute path.</returns>
    internal static string Relative(string root, string path)
    {
        return StoragePaths.IsUnder(root, path)
            ? GameToolPattern.Normalize(Path.GetRelativePath(root, path))
            : Path.GetFullPath(path);
    }

    /// <summary>The forward-slash relative path of a directory under a root, or the directory itself outside it.</summary>
    /// <param name="root">The install root.</param>
    /// <param name="directory">The directory.</param>
    /// <returns>An empty string for the root itself, the relative path below it, or the directory unchanged outside it.</returns>
    internal static string RelativeDirectory(string root, string directory)
    {
        if (!StoragePaths.IsUnder(root, directory))
        {
            return directory;
        }

        var relative = GameToolPattern.Normalize(Path.GetRelativePath(root, directory));
        return relative == "." ? "" : relative;
    }
}
