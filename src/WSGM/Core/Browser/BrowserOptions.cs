using System;

namespace WSGM.Core;

/// <summary>Persisted browser preferences. These do not govern Steam-owned browser processes.</summary>
public sealed record BrowserOptions
{
    /// <summary>Defaults to releasing browser memory when the lookup ends.</summary>
    public BrowserMemoryBehavior MemoryBehavior { get; set; }

    /// <summary>Remembered page zoom, independent of Windows display scaling.</summary>
    public double ZoomFactor { get; set; } = 1;

    /// <summary>Returns a detached, valid preference snapshot.</summary>
    /// <returns>A snapshot safe to retain across configuration replacement.</returns>
    public BrowserOptions Normalize()
    {
        return new BrowserOptions
        {
            MemoryBehavior = Enum.IsDefined(MemoryBehavior) ? MemoryBehavior : BrowserMemoryBehavior.UnloadWhenClosed,
            ZoomFactor = double.IsFinite(ZoomFactor) ? Math.Clamp(ZoomFactor, 0.5, 3) : 1
        };
    }
}
