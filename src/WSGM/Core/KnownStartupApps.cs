using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>Companion utilities handheld users typically want running before the
/// launcher — offered as one-click suggestions with sane elevation defaults.</summary>
public static class KnownStartupApps
{
    private sealed record Suggestion(string Label, string[] RelativePaths, bool Elevated);

    private static readonly Suggestion[] Candidates =
    [
        // Handheld Companion needs elevation for its virtual controller / HID work.
        new("Handheld Companion", ["Handheld Companion\\HandheldCompanion.exe"], true),
        new("HandheldCompanion (legacy path)", ["HandheldCompanion\\HandheldCompanion.exe"], true),
        new("RTSS (RivaTuner Statistics Server)", ["RivaTuner Statistics Server\\RTSS.exe"], true),
        new("MSI Afterburner", ["MSI Afterburner\\MSIAfterburner.exe"], true),
        new("Playnite (desktop)", ["Playnite\\Playnite.DesktopApp.exe"], false),
    ];

    /// <summary>(label, full path, elevated) for each suggestion found on disk.</summary>
    public static List<(string Label, string Path, bool Elevated)> Detected()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        var found = new List<(string, string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates)
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }
                foreach (var relative in candidate.RelativePaths)
                {
                    try
                    {
                        var full = Path.Combine(root, relative);
                        if (File.Exists(full) && seen.Add(Path.GetFileName(full)))
                        {
                            found.Add((candidate.Label, full, candidate.Elevated));
                        }
                    }
                    catch { /* unreadable root */ }
                }
            }
        }
        return found;
    }
}
