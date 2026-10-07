// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.IO;

namespace WSGM.Core;

internal static class StoragePaths
{
    internal static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
