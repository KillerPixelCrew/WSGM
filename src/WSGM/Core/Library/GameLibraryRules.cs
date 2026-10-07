using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Normalizes the stored library configuration in place before consumers use it.</summary>
internal static class GameLibraryRules
{
    /// <summary>
    ///     Repairs the source and folder lists of a Game Library section. Its enums are repaired with the rest
    ///     of the document by <see cref="AppConfigRules" />.
    /// </summary>
    /// <param name="library">The section to repair in place.</param>
    /// <returns>An empty diagnostic list; these repairs do not produce warning entries.</returns>
    internal static IReadOnlyList<string> Normalize(GameLibraryConfig library)
    {
        library.DisabledSources =
        [
            .. (library.DisabledSources ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        // A folder needs an identity no other folder has and an absolute path; its file types are
        // the few a shortcut can be, so a hand edit cannot turn a folder of documents into titles.
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        library.ShortcutFolders =
        [
            .. (library.ShortcutFolders ?? [])
            .Where(folder => folder is { Id.Length: > 7, Path.Length: > 2 }
                             && folder.Id.StartsWith("folder:", StringComparison.Ordinal)
                             && Path.IsPathFullyQualified(folder.Path)
                             && ids.Add(folder.Id))
        ];
        foreach (var folder in library.ShortcutFolders)
        {
            folder.Extensions =
            [
                .. (folder.Extensions ?? [])
                .Where(extension => ShortcutFolderConfig.AllowedExtensions.Contains(extension, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
            ];
            if (folder.Extensions.Count == 0)
            {
                folder.Extensions = [.. ShortcutFolderConfig.AllowedExtensions];
            }
        }

        return [];
    }
}
