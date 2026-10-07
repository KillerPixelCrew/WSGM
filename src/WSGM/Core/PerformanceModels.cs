using System;

namespace WSGM.Core;

/// <summary>The two bounded RTSS controls exposed through shared performance state.</summary>
internal enum PerformanceControl
{
    /// <summary>RTSS frame limit in frames per second; zero removes the limit.</summary>
    FrameLimit,

    /// <summary>WSGM OSD selector: off, three presets, or the custom layout.</summary>
    OverlayLevel
}

/// <summary>Lifecycle of the last semantic performance command.</summary>
internal enum PerformanceCommandPhase
{
    /// <summary>No command has been submitted.</summary>
    Idle,

    /// <summary>The command is awaiting serialized adapter access.</summary>
    Queued,

    /// <summary>The command is validating, persisting, or writing its value.</summary>
    Applying,

    /// <summary>The preference is saved while the application executable is still unknown.</summary>
    Deferred,

    /// <summary>The adapter accepted the write; immediate readback is not required.</summary>
    Applied,

    /// <summary>The command was refused by admission, validation, or the adapter.</summary>
    Rejected,

    /// <summary>Persistence or processing failed, timed out, or was cancelled after admission.</summary>
    Failed,

    /// <summary>Polling detected a value different from the previous observation.</summary>
    ExternalChange
}

/// <summary>Canonical WSGM application identity plus optional Steam and RTSS enrichment.</summary>
/// <remarks>
///     <see cref="ApplicationId" /> is authoritative whenever this record exists. Steam can name a game
///     before Windows exposes its foreground executable, so <see cref="RtssProfileName" /> is optional:
///     policy remains per-application. The frame limit waits for enrichment; the overlay level
///     applies through the global RTSS profile meanwhile.
/// </remarks>
/// <param name="ApplicationId">Authoritative WSGM application identifier.</param>
/// <param name="SteamAppId">Steam identity, when known.</param>
/// <param name="RtssProfileName">Executable profile name, or null until executable identity is available.</param>
/// <param name="ProcessId">Known application process, or null.</param>
internal sealed record PerformanceApplicationTarget(
    string ApplicationId,
    uint? SteamAppId,
    string? RtssProfileName,
    int? ProcessId = null);

/// <summary>Desired or observed values. Null means the corresponding control has no value.</summary>
/// <param name="FrameLimit">Frame limit in frames per second; zero is unlimited, null is unspecified.</param>
/// <param name="OverlayLevel">WSGM overlay selector value, or null when unspecified.</param>
internal sealed record PerformanceValues(int? FrameLimit, int? OverlayLevel)
{
    /// <summary>No value for either control.</summary>
    internal static readonly PerformanceValues Empty = new(null, null);

    /// <summary>Returns the value for one control.</summary>
    /// <param name="control">The control to select.</param>
    /// <returns>The stored value, or null for an unknown control.</returns>
    internal int? ValueFor(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => FrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevel,
            _ => null
        };
    }

    /// <summary>Returns a copy with one control changed.</summary>
    /// <param name="control">The control to change.</param>
    /// <param name="value">The replacement value; this method does not validate bounds.</param>
    /// <returns>The updated copy, or this instance for an unknown control.</returns>
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
/// <param name="Sequence">Monotonically increasing service command identifier; zero is the idle sentinel.</param>
/// <param name="Origin">Sanitized name of the submitting surface or workflow.</param>
/// <param name="CorrelationId">Sanitized request token for diagnostics.</param>
/// <param name="Control">Control addressed by this command.</param>
/// <param name="RequestedValue">Requested value, or null when no command exists.</param>
/// <param name="Phase">Current processing outcome or stage.</param>
/// <param name="Diagnostic">Explanation of the stage or failure, when available.</param>
internal sealed record PerformanceCommandState(
    long Sequence,
    string Origin,
    string CorrelationId,
    PerformanceControl Control,
    int? RequestedValue,
    PerformanceCommandPhase Phase,
    string? Diagnostic)
{
    /// <summary>Initial state before any command is submitted.</summary>
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
