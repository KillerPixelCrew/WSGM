using System;

namespace WSGM.Core;

/// <summary>The two bounded RTSS controls exposed through shared performance state.</summary>
internal enum PerformanceControl
{
    FrameLimit,
    OverlayLevel
}

/// <summary>Lifecycle of the last semantic performance command.</summary>
internal enum PerformanceCommandPhase
{
    Idle,
    Queued,
    Applying,
    Deferred,
    Applied,
    Rejected,
    Failed,
    ExternalChange
}

/// <summary>Canonical WSGM application identity plus optional Steam and RTSS enrichment.</summary>
/// <remarks>
///     <see cref="ApplicationId" /> is authoritative whenever this record exists. Steam can name a game
///     before Windows exposes its foreground executable, so <see cref="RtssProfileName" /> is optional:
///     policy remains per-application. The frame limit waits for enrichment; the overlay level
///     applies through the global RTSS profile meanwhile.
/// </remarks>
internal sealed record PerformanceApplicationTarget(
    string ApplicationId,
    uint? SteamAppId,
    string? RtssProfileName,
    int? ProcessId = null);

/// <summary>Desired or observed values. Null means the corresponding control has no value.</summary>
internal sealed record PerformanceValues(int? FrameLimit, int? OverlayLevel)
{
    internal static readonly PerformanceValues Empty = new(null, null);

    internal int? ValueFor(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => FrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevel,
            _ => null
        };
    }

    internal PerformanceValues With(PerformanceControl control, int value)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => this with { FrameLimit = value },
            PerformanceControl.OverlayLevel => this with { OverlayLevel = value },
            _ => this
        };
    }
}

/// <summary>Immutable command status shared by every performance UI client.</summary>
internal sealed record PerformanceCommandState(
    long Sequence,
    string Origin,
    string CorrelationId,
    PerformanceControl Control,
    int? RequestedValue,
    PerformanceCommandPhase Phase,
    string? Diagnostic)
{
    internal static readonly PerformanceCommandState Idle = new(
        0,
        string.Empty,
        string.Empty,
        PerformanceControl.FrameLimit,
        null,
        PerformanceCommandPhase.Idle,
        null);
}

/// <summary>Immutable RTSS state projected into the overlay and native QAM.</summary>
/// <remarks>
///     <see cref="Observed" /> is what RTSS last read back, or the value WSGM last wrote there, and
///     empty while RTSS is unavailable. An unknown executable leaves the frame limit unobserved,
///     while the overlay level is read from the global profile.
/// </remarks>
internal sealed record PerformanceState(
    RtssProbe Probe,
    PerformanceApplicationTarget? Target,
    bool ApplicationProfileEnabled,
    ProfileSource FrameLimitLayer,
    ProfileSource OverlayLevelLayer,
    PerformanceValues Desired,
    PerformanceValues Observed,
    DateTimeOffset? RefreshedAt,
    PerformanceCommandState Command);
