namespace WSGM.Core;

/// <summary>The frame rate AutoTDP holds a game to, and how it meets an active frame limit.</summary>
/// <remarks>
///     The target is AutoTDP's own per-game profile value, so control needs no frame limit. A frame limit
///     below the target wins: the game cannot render faster than its cap, and judging it against a higher
///     target would read every capped window as a miss and raise power to the ceiling. A limit at or
///     above the target changes nothing, because the controller lowers power until the game runs just
///     above the target.
/// </remarks>
internal static class AutoTdpTarget
{
    /// <summary>Lowest target offered.</summary>
    internal const int MinimumFps = 20;

    /// <summary>Highest target offered.</summary>
    internal const int MaximumFps = 144;

    /// <summary>The target when no profile layer sets one.</summary>
    internal const int DefaultFps = 60;

    /// <summary>Whether a stored target lies within the offered range.</summary>
    /// <param name="fps">The target.</param>
    /// <returns>True when it can be used.</returns>
    internal static bool IsValid(int fps)
    {
        return fps is >= MinimumFps and <= MaximumFps;
    }

    /// <summary>The frame rate control aims for.</summary>
    /// <param name="targetFps">The resolved profile target, or null for the default.</param>
    /// <param name="activeFrameLimit">The frame limit RTSS is holding now, or null without one.</param>
    /// <returns>The lower of the target and an active frame limit.</returns>
    internal static int EffectiveFps(int? targetFps, int? activeFrameLimit)
    {
        var fps = targetFps is { } target && IsValid(target) ? target : DefaultFps;
        return activeFrameLimit is > 0 and { } limit && limit < fps ? limit : fps;
    }
}
