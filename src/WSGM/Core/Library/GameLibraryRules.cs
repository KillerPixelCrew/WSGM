using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

internal static class GameLibraryRules
{
    /// <summary>Canonical extensions from either token lists or space/comma/semicolon separated input.</summary>
    internal static List<string> NormalizeExtensions(IEnumerable<string>? values)
    {
        return
        [
            .. (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value =>
                value.Split([' ', '\t', '\r', '\n', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => "." + value.Trim().TrimStart('.').ToLowerInvariant())
            .Where(value => value.Length > 1 && value.IndexOfAny(['/', '\\', '\0']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    ///     Repairs the source and folder lists of a Game Library section. Its enums are repaired with the rest
    ///     of the document by <see cref="AppConfigRules" />.
    /// </summary>
    /// <param name="library">The section to repair in place.</param>
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
            .Where(folder => folder is { Id.Length: > 0, Root.AbsolutePath.Length: > 2 }
                             && Path.IsPathFullyQualified(folder.Root.AbsolutePath)
                             && ids.Add(folder.Id))
        ];
        foreach (var folder in library.ShortcutFolders)
        {
            folder.Extensions =
            [
                .. NormalizeExtensions(folder.Extensions)
                    .Where(extension =>
                        ShortcutFolderConfig.AllowedExtensions.Contains(extension, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
            ];
            if (folder.Extensions.Count == 0)
            {
                folder.Extensions = [.. ShortcutFolderConfig.AllowedExtensions];
            }
        }

        library.RomSources =
        [
            .. (library.RomSources ?? []).Where(source => source is { Id.Length: > 0, Root.AbsolutePath.Length: > 2 }
                                                          && source.Root.Directory &&
                                                          Path.IsPathFullyQualified(source.Root.AbsolutePath)
                                                          && source.SystemId.Length > 0 && ids.Add(source.Id))
        ];
        foreach (var source in library.RomSources)
        {
            source.SystemId = EmulatorStorage.NormalizeSystemId(source.SystemId);
            source.Extensions = NormalizeExtensions(source.Extensions);
            source.Exclusions = [.. (source.Exclusions ?? []).Where(value => !string.IsNullOrWhiteSpace(value))];
            source.Arguments = [.. (source.Arguments ?? []).Where(value => !value.Contains('\0'))];
        }

        library.ManualSources =
        [
            .. (library.ManualSources ?? []).Where(source =>
                source is { Id.Length: > 0, Name.Length: > 0 }
                && (source.RomPath is { AbsolutePath.Length: > 0 } || Path.IsPathFullyQualified(source.Target))
                && ids.Add(source.Id))
        ];

        return [];
    }
}
