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
        // Handheld Companion needs elevation for its virtual controller / HID work.
        new("Handheld Companion", ["Handheld Companion\\HandheldCompanion.exe"], true),
        new("HandheldCompanion (legacy path)", ["HandheldCompanion\\HandheldCompanion.exe"], true),
        new("RTSS (RivaTuner Statistics Server)", ["RivaTuner Statistics Server\\RTSS.exe"], true),
        new("MSI Afterburner", ["MSI Afterburner\\MSIAfterburner.exe"], true),
        new("Playnite (desktop)", ["Playnite\\Playnite.DesktopApp.exe"], false),
        // Windows starts AMD Software's background host from its "StartCN" Run entry, which Explorer
        // does not reach before Game Mode retires it. AMD's service-wide AFMF and RSR settings are
        // reported to take effect only while that host runs, so it is offered with the same arguments.
        new("AMD Software: Adrenalin Edition (background host)", ["AMD\\CNext\\CNext\\cncmd.exe"], false,
            "restart")
    ];

    /// <summary>(label, full path, arguments, elevated) for each suggestion found on disk.</summary>
    /// <returns>New suggestion list in catalog/root priority order, deduplicated by executable basename.</returns>
    public static List<(string Label, string Path, string Args, bool Elevated)> Detected()
    {
        return Resolve(
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ], File.Exists);
    }

    /// <summary>Resolves the catalog against the given roots and an existence check.</summary>
    /// <param name="roots">Install roots in priority order; empty entries are skipped.</param>
    /// <param name="exists">Whether a candidate path is present.</param>
    /// <returns>New suggestion list in catalog/root priority order, deduplicated by executable basename.</returns>
    internal static List<(string Label, string Path, string Args, bool Elevated)> Resolve(
        IReadOnlyList<string> roots, Func<string, bool> exists)
    {
        // Dedupe key is the bare exe FILENAME, deliberately across roots and across
        // candidates: the legacy-path entry and multi-root (x86/x64) installs of the
        // same tool collapse to the first hit in priority order. Full-path dedupe
        // would resurface those as duplicate suggestions.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return (from candidate in Candidates
            from root in roots
            where !string.IsNullOrEmpty(root)
            from relative in candidate.RelativePaths
            let full = Path.Combine(root, relative)
            where exists(full) && seen.Add(Path.GetFileName(full))
            select (candidate.Label, full, candidate.Args, candidate.Elevated)).ToList();
    }

    private sealed record Suggestion(string Label, string[] RelativePaths, bool Elevated, string Args = "");
}
