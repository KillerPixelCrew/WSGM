using System;

namespace WSGM.Core;

/// <summary>What happens to the WSGM browser when its page or Overlay closes.</summary>
public enum BrowserMemoryBehavior
{
    /// <summary>Close the controller and its helper; preserve only the on-disk site profile.</summary>
    UnloadWhenClosed,

    /// <summary>Retain the page, mute it and try to suspend it until the page opens again.</summary>
    KeepAlive
}

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

/// <summary>Locally resolved context offered only when the user chooses game-aware search.</summary>
/// <param name="AppId">The game's stable Steam identifier.</param>
/// <param name="Title">Display title; never transmitted automatically.</param>
internal sealed record BrowserGameContext(string AppId, string Title);

/// <summary>The browser engine's actual resource lifetime, separate from page navigation.</summary>
internal enum BrowserLifecycle
{
    NotCreated,
    Creating,
    Active,
    Retained,
    Destroying,
    Failed
}
