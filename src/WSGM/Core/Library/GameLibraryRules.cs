using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class GameLibraryRules
{
    /// <summary>Repairs a Game Library section a hand edit left naming no mode that exists.</summary>
    /// <param name="library">The section to repair in place.</param>
    internal static IReadOnlyList<string> Normalize(GameLibraryConfig library)
    {
        ConfigRepair.NormalizeEnums(library);
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
