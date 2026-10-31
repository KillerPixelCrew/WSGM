using System;

namespace WSGM.Core;

/// <summary>Immutable RTSS state projected into the overlay and native QAM.</summary>
/// <remarks>
///     <see cref="Observed" /> is what RTSS last read back, or the value WSGM last wrote there, and
///     empty while RTSS is unavailable. An unknown executable leaves the frame limit unobserved,
///     while the overlay level comes from the adapter's WSGM renderer state.
/// </remarks>
/// <param name="Probe">Latest discovery and adapter availability result.</param>
/// <param name="Target">Running application identity, or null for the global target.</param>
/// <param name="ApplicationProfileEnabled">Whether the active application uses its own WSGM profile.</param>
/// <param name="FrameLimitLayer">Profile layer supplying the resolved frame limit.</param>
/// <param name="OverlayLevelLayer">Profile layer supplying the resolved overlay level.</param>
/// <param name="Desired">Values resolved from the current WSGM profile policy.</param>
/// <param name="Observed">Last values read or successfully written; not proof of hardware readback.</param>
/// <param name="RefreshedAt">Time of the last observation, published write, or unavailable refresh.</param>
/// <param name="Command">Most recent semantic command status.</param>
/// <param name="OverlayPresentationEnabled">RTSS's presentation gates; separate from the selected overlay level.</param>
internal sealed record PerformanceState(
    RtssProbe Probe,
    PerformanceApplicationTarget? Target,
    bool ApplicationProfileEnabled,
    ProfileSource FrameLimitLayer,
    ProfileSource OverlayLevelLayer,
    PerformanceValues Desired,
    PerformanceValues Observed,
    DateTimeOffset? RefreshedAt,
    PerformanceCommandState Command,
    bool? OverlayPresentationEnabled = null);
