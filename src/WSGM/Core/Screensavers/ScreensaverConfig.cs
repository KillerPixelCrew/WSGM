using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Local screensaver sources. Steam retains its own selected screensaver and timeouts.</summary>
public sealed class ScreensaverConfig
{
    /// <summary>Image folders and local manifest-based packs registered by the user.</summary>
    public List<ScreensaverSourceConfig> Sources { get; set; } = [];

    /// <summary>Repairs persisted options without accessing any source folder.</summary>
    public void Normalize()
    {
        Sources = (Sources ?? []).Where(source => source is not null
                && !string.IsNullOrWhiteSpace(source.Id) && source.Id.Length <= 64
                && !string.IsNullOrWhiteSpace(source.Name) && source.Name.Length <= 128
                && !source.Name.Any(char.IsControl) && source.Folder is { Length: <= 4096 })
            .DistinctBy(source => source.Id, StringComparer.Ordinal)
            .DistinctBy(source => source.Name.Trim(), StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        foreach (var source in Sources)
        {
            source.Name = (source.Name ?? "").Trim();
            source.Folder ??= "";
            source.IntervalSeconds = Math.Clamp(source.IntervalSeconds, 2, 3600);
            source.FramesPerSecond = Math.Clamp(source.FramesPerSecond, 1, 60);
        }
    }
}

/// <summary>One user-approved image folder or pack, referenced in place rather than copied.</summary>
public sealed class ScreensaverSourceConfig
{
    /// <summary>Stable WSGM identity, independent of drive availability.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name shown in Steam's native chooser and the Overlay.</summary>
    public string Name { get; set; } = "";

    /// <summary>The absolute image-folder or pack-root path.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Whether the folder contains a screensaver.json manifest.</summary>
    public bool Pack { get; set; }

    /// <summary>Shuffle images; false plays their sorted filenames in order.</summary>
    public bool Shuffle { get; set; } = true;

    /// <summary>Image transition interval, from two seconds to one hour.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Crop to fill the display; false preserves the whole image with aspect fit.</summary>
    public bool Fill { get; set; }

    /// <summary>Maximum scene animation callback rate; lower values reduce idle GPU work.</summary>
    public int FramesPerSecond { get; set; } = 30;
}
