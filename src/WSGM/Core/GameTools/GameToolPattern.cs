using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace WSGM.Core;

/// <summary>DOS wildcard matching over forward-slash relative paths.</summary>
internal static class GameToolPattern
{
    /// <summary>Matches one pattern.</summary>
    /// <param name="pattern">A pattern containing <c>/</c> matches the relative path only; others also match the name.</param>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>Whether it matches, ignoring case.</returns>
    internal static bool Matches(string pattern, string relativePath)
    {
        if (pattern.Contains('/'))
        {
            return FileSystemName.MatchesSimpleExpression(pattern, relativePath);
        }

        return FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(relativePath))
               || FileSystemName.MatchesSimpleExpression(pattern, relativePath);
    }

    /// <summary>Matches any of several patterns.</summary>
    /// <param name="patterns">The patterns.</param>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>Whether one matches.</returns>
    internal static bool MatchesAny(IEnumerable<string> patterns, string relativePath)
    {
        return patterns.Any(pattern => Matches(pattern, relativePath));
    }

    /// <summary>Converts an OS relative path to the catalog's forward-slash form.</summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The normalized path.</returns>
    internal static string Normalize(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/');
    }
}
