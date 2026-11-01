// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>Whether two paths name the same file or directory, ignoring case and trailing separators.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns>True when both normalize to the same full path; false when either is not a valid path.</returns>
    internal static bool Same(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>A per-user named mutex name derived from a data root, so two roots never share a lock.</summary>
    /// <param name="prefix">The owner's name prefix, such as <c>Local\WSGM.Emulators.</c>.</param>
    /// <param name="userRoot">The per-user data root.</param>
    /// <returns>The prefix followed by 32 hex characters of the root's hash.</returns>
    internal static string GateName(string prefix, string userRoot)
    {
        return prefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(userRoot).ToUpperInvariant())))[..32];
    }
}
