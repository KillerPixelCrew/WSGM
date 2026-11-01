using System;

namespace WSGM.Core;

/// <summary>
///     One AutoTDP trace row. Nullable observations are absent when unavailable;
///     an empty CSV cell must not be interpreted as zero.
/// </summary>
/// <remarks>
///     Raw RTSS window bounds and timings preserve the evidence used for a controller decision.
///     Device observations may be the last published write rather than a hardware readback.
/// </remarks>
internal sealed class AutoTdpTraceRow
{
    /// <summary>Event that caused this row.</summary>
    internal AutoTdpTraceEvent Event { get; init; }

    /// <summary>One-based row sequence within the trace, assigned at commit.</summary>
    internal long Row { get; set; }

    /// <summary>Control clock supplied by the caller, or milliseconds since this trace began.</summary>
    internal double ElapsedMs { get; set; }

    /// <summary>The control clock the caller judged this tick on, when it owns one.</summary>
    internal double? ControlClockMs { get; init; }

    /// <summary>Wall-clock timestamp captured when the row began.</summary>
    internal DateTimeOffset WallClock { get; set; }

    /// <summary>Milliseconds since the previous committed tick; null for the first tick.</summary>
    internal double? TickIntervalMs { get; set; }

    /// <summary>Identifier shared by rows in the current recording.</summary>
    internal string? TraceId { get; set; }

    /// <summary>Application version recorded with this row.</summary>
    internal string? WsgmVersion { get; set; }

    /// <summary>Active device package identity, cached for event rows.</summary>
    internal string? Package { get; set; }

    /// <summary>Active device package version, cached for event rows.</summary>
    internal string? PackageVersion { get; set; }

    /// <summary>Authoritative running application identity, when known.</summary>
    internal string? ApplicationId { get; set; }

    /// <summary>Running application executable identity, when known.</summary>
    internal string? Executable { get; set; }

    /// <summary>Process identity of the selected RTSS renderer.</summary>
    internal uint? ProcessId { get; set; }

    /// <summary>Running-application snapshot generation.</summary>
    internal long? RunningGeneration { get; set; }

    /// <summary>Controller operating-point identity used to detect a required rebase.</summary>
    internal string? ContextKey { get; set; }

    /// <summary>Resolved target frametime in milliseconds.</summary>
    internal double? TargetFrametimeMs { get; set; }

    /// <summary>Windows AC-line state, when available.</summary>
    internal bool? AcPower { get; set; }

    /// <summary>Windows battery percentage, when available.</summary>
    internal double? BatteryPercent { get; set; }

    /// <summary>Effective Windows power-mode name.</summary>
    internal string? PowerMode { get; set; }

    /// <summary>Active Windows power-scheme GUID text.</summary>
    internal string? PowerScheme { get; set; }

    /// <summary>Resolved sustained-power minimum and ceiling for this decision.</summary>
    internal AutoTdpLimits? Limits { get; set; }

    /// <summary>Device capability identifier for the sustained limit.</summary>
    internal string? PowerCapability { get; set; }

    /// <summary>Paired power-limit capability identifier, when applicable.</summary>
    internal string? PairedCapability { get; set; }

    /// <summary>Published sustained-power value before control; may be the last written value.</summary>
    internal long? ObservedWatts { get; set; }

    /// <summary>Quality of the published sustained-power projection.</summary>
    internal string? ObservedQuality { get; set; }

    /// <summary>Published paired-limit value before control.</summary>
    internal long? PairedObservedWatts { get; set; }

    /// <summary>Number of live RTSS renderers considered for sample selection.</summary>
    internal int? Renderers { get; set; }

    /// <summary>Sample-selection result: none, executable, only-renderer, or ambiguous.</summary>
    internal string? Selection { get; set; }

    /// <summary>Selected RTSS averaging window, including raw bounds and age.</summary>
    internal RtssFrametimeSample? Frametime { get; set; }

    /// <summary>Whether the selected window matches the previous window for this renderer.</summary>
    internal bool? WindowRepeat { get; set; }

    /// <summary>Signed gap from the previous window end to this window start, using wrapping RTSS ticks.</summary>
    internal long? WindowGapMs { get; set; }

    /// <summary>Whether this tick started the controller.</summary>
    internal bool? ControllerStarted { get; set; }

    /// <summary>
    ///     The limit the controller took as current when it started or re-based: the observed
    ///     value, else the value last written, else the ceiling on a device without readback.
    /// </summary>
    internal int? StartWatts { get; set; }

    /// <summary>Whether this tick rebased the controller to a changed operating point.</summary>
    internal bool? Rebased { get; set; }

    /// <summary>Controller diagnostic snapshot associated with this row.</summary>
    internal AutoTdpControllerSnapshot? Controller { get; set; }

    /// <summary>Recording-local probe sequence for a probe or its restoration.</summary>
    internal long? ProbeId { get; set; }

    /// <summary>Controller action and requested power for this tick.</summary>
    internal AutoTdpDecision? Decision { get; set; }

    /// <summary>Controller power before the decision, used to derive the requested delta.</summary>
    internal int? PreviousWatts { get; set; }

    /// <summary>Milliseconds since the preceding committed decision that required a write.</summary>
    internal double? SinceDecisionMs { get; set; }

    /// <summary>Milliseconds since the preceding committed dispatched write.</summary>
    internal double? SinceWriteMs { get; set; }

    /// <summary>Whether a device write was submitted for this row.</summary>
    internal bool? WriteDispatched { get; set; }

    /// <summary>Device command outcome token.</summary>
    internal string? WriteOutcome { get; set; }

    /// <summary>Optional readback carried by the command result; absence or disagreement does not gate acceptance.</summary>
    internal long? WriteReadbackWatts { get; set; }

    /// <summary>Whether the command outcome counts as applied under device-write policy.</summary>
    internal bool? WriteApplied { get; set; }

    /// <summary>Elapsed time awaiting the dispatched device command, in milliseconds.</summary>
    internal double? WriteMs { get; set; }

    /// <summary>Diagnostic reason a write was skipped or failed.</summary>
    internal string? WriteNote { get; set; }

    /// <summary>Published sustained-power projection after the write attempt.</summary>
    internal long? PostObservedWatts { get; set; }

    /// <summary>Published paired-limit projection after the write attempt.</summary>
    internal long? PostPairedObservedWatts { get; set; }

    /// <summary>Shared RTSS sensor sample used as advisory controller evidence.</summary>
    internal RtssOsdMetrics? Metrics { get; set; }

    /// <summary>AutoTDP service state token for this row.</summary>
    internal string? Status { get; set; }

    /// <summary>Additional diagnostic context for the service state.</summary>
    internal string? Detail { get; set; }
}
