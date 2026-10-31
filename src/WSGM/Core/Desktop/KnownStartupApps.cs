using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     Companion utilities handheld users typically want running before the
///     launcher — offered as one-click suggestions with sane elevation defaults.
/// </summary>
public static class KnownStartupApps
{
    private static readonly Suggestion[] Candidates =
    [
        new("MSI Afterburner", ["MSI Afterburner\\MSIAfterburner.exe"], true),
        new("Playnite (desktop)", ["Playnite\\Playnite.DesktopApp.exe"], false)
    ];

    /// <summary>(label, full path, elevated) for each suggestion found on disk.</summary>
    /// <returns>New suggestion list in catalog/root priority order, deduplicated by executable basename.</returns>
    /// <remarks>
    ///     A program WSGM turns off as a competing manager, or RTSS, which WSGM starts and keeps running itself, is
    ///     never suggested, so the list cannot contradict either.
    /// </remarks>
    public static List<(string Label, string Path, bool Elevated)> Detected()
    {
        var excluded = new HashSet<string>(
            [
                .. OtherManagers.Known.SelectMany(manager => manager.Processes),
                Path.GetFileNameWithoutExtension(RtssDiscovery.ExecutableName)
            ],
            StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        // Dedupe key is the bare exe FILENAME, deliberately across roots and across
        // candidates: multi-root (x86/x64) installs of the same tool collapse to the
        // first hit in priority order. Full-path dedupe would resurface those as
        // duplicate suggestions.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return (from candidate in Candidates
            from root in roots
            where !string.IsNullOrEmpty(root)
            from relative in candidate.RelativePaths
            let full = Path.Combine(root, relative)
            where !excluded.Contains(Path.GetFileNameWithoutExtension(full))
                  && File.Exists(full) && seen.Add(Path.GetFileName(full))
            select (candidate.Label, full, candidate.Elevated)).ToList();
    }

    private sealed record Suggestion(string Label, string[] RelativePaths, bool Elevated);
}
