namespace WSGM.Core;

/// <summary>How a frame limit relates to the panel's refresh rate.</summary>
/// <remarks>
///     A user setting rather than a fixed policy, because the right answer differs per device and per
///     tolerance for mode changes. A mode change is not free: an exclusive-fullscreen title can hitch,
///     minimize, or drop out across one.
/// </remarks>
public enum FrameLimitStrategy
{
    /// <summary>
    ///     Cap frames and never touch the refresh rate. The default, and the right answer wherever
    ///     variable refresh covers the range, because it changes no display state at all.
    /// </summary>
    FrameLimitOnly,

    /// <summary>
    ///     Cap frames, and switch refresh only among the panel's own advertised modes.
    /// </summary>
    NativeModes,

    /// <summary>
    ///     Cap frames, and pick the lowest driver-accepted mode that shows every frame at least twice —
    ///     including modes synthesized beyond what the panel advertises. A doubled cadence is what lets
    ///     adaptive sync's low-framerate compensation smooth the presentation; holding a 30 FPS cap at
    ///     30 Hz keeps that machinery out of reach. When no doubled multiple exists the lowest exact
    ///     multiple still wins, and failing that the lowest mode that can present the cap.
    /// </summary>
    FrameDoubling
}
