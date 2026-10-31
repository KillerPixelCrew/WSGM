using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Remembers the PE architecture of executables across scans while their size and time are unchanged.</summary>
/// <remarks>Each scan opens every executable of a game; a rescan of an unchanged folder then reads none.</remarks>
internal sealed class PeArchitectureCache
{
    private const int Capacity = 4096;

    private readonly Dictionary<string, (long Size, DateTime LastWriteUtc, string? Architecture)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    /// <summary>The architecture a program runs as, read from the file only when it changed.</summary>
    /// <param name="file">The walked file.</param>
    /// <returns>The architecture, or null when the file is not a readable PE image.</returns>
    internal string? Read(GameFolderFile file)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(file.FullPath, out var entry) && entry.Size == file.Size
                                                                  && entry.LastWriteUtc == file.LastWriteUtc)
            {
                return entry.Architecture;
            }
        }

        var architecture = PeImage.TryArchitecture(file.FullPath);
        lock (_sync)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Clear();
            }

            _entries[file.FullPath] = (file.Size, file.LastWriteUtc, architecture);
        }

        return architecture;
    }
}
