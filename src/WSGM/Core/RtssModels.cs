using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Availability of the optional external RTSS integration.</summary>
internal enum RtssAvailability
{
    /// <summary>Discovery has not run.</summary>
    Unknown,

    /// <summary>No RTSS registration was found.</summary>
    NotInstalled,

    /// <summary>Registration, file identity, architecture, or required exports were not accepted.</summary>
    Incompatible,

    /// <summary>The verified installation has no matching running process.</summary>
    NotRunning,

    /// <summary>Discovery accepted a running process; the profile API is not yet ready.</summary>
    AdapterUnavailable,

    /// <summary>The adapter can access the verified RTSS profile API.</summary>
    Ready,

    /// <summary>Discovery or adapter access failed, or runtime identity was ambiguous.</summary>
    Degraded
}

/// <summary>Bounds reported by a concrete RTSS adapter.</summary>
/// <param name="MinimumFrameLimit">Inclusive minimum frame limit in frames per second.</param>
/// <param name="MaximumFrameLimit">Inclusive maximum frame limit in frames per second.</param>
/// <param name="OverlayLevels">Supported WSGM overlay selector values; callers must not mutate the set.</param>
internal sealed record RtssCapabilities(
    int MinimumFrameLimit,
    int MaximumFrameLimit,
    IReadOnlySet<int> OverlayLevels)
{
    /// <summary>Checks whether bounds are available for a control.</summary>
    /// <param name="control">The control to inspect.</param>
    /// <returns>True for a valid frame-limit range or a nonempty overlay-level set.</returns>
    internal bool Supports(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => MinimumFrameLimit >= 0
                                             && MaximumFrameLimit >= MinimumFrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevels.Count > 0,
            _ => false
        };
    }

    /// <summary>Checks a value against the reported bounds.</summary>
    /// <param name="control">The control to inspect.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>True when the value is in range or in the supported set; false for unknown controls.</returns>
    internal bool IsValid(PerformanceControl control, int value)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => value >= MinimumFrameLimit && value <= MaximumFrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevels.Contains(value),
            _ => false
        };
    }
}

/// <summary>One bounded adapter discovery result. Process identity is folded into Generation.</summary>
/// <param name="Availability">Discovery or adapter readiness state.</param>
/// <param name="Version">Registered RTSS version, when identified.</param>
/// <param name="ExecutablePath">Verified candidate executable path, when identified.</param>
/// <param name="Generation">Process/install identity token used to reject stale requests; zero without an identity.</param>
/// <param name="Capabilities">Verified control bounds when the adapter is ready.</param>
/// <param name="Diagnostic">Availability explanation suitable for diagnostics.</param>
/// <param name="ProcessId">Matching running RTSS process, when uniquely identified.</param>
internal sealed record RtssProbe(
    RtssAvailability Availability,
    string? Version,
    string? ExecutablePath,
    long Generation,
    RtssCapabilities? Capabilities,
    string? Diagnostic,
    int? ProcessId = null);

/// <summary>Result of querying the active global or application profile.</summary>
/// <param name="Values">Frame-limit profile value and the adapter's current WSGM overlay level.</param>
/// <param name="Timestamp">UTC time assigned by the adapter to this observation.</param>
internal sealed record RtssReadback(PerformanceValues Values, DateTimeOffset Timestamp);

/// <summary>Narrow property update sent to the adapter.</summary>
/// <param name="RtssProfileName">Executable profile name, or the empty string for the global profile.</param>
/// <param name="Control">Single control to write.</param>
/// <param name="Value">Proposed value, validated by the adapter.</param>
/// <param name="Generation">Expected identity from the probe that admitted this request.</param>
internal sealed record RtssApplyRequest(
    string RtssProfileName,
    PerformanceControl Control,
    int Value,
    long Generation);

/// <summary>Result of an adapter mutation attempt.</summary>
/// <param name="Applied">Whether the adapter accepted the mutation; does not require a following readback.</param>
/// <param name="Diagnostic">Reason for refusal, when available.</param>
internal sealed record RtssApplyResult(bool Applied, string? Diagnostic);

/// <summary>Adapter boundary used by the shared service and deterministic tests.</summary>
internal interface IRtssAdapter : IAsyncDisposable
{
    /// <summary>
    ///     Applies the Custom overlay's configuration (selector level 4). A cheap handoff
    ///     to the adapter's renderer; adapters without one ignore it.
    /// </summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    void ApplyOsdCustomization(RtssOsdCustomSettings settings);

    /// <summary>Applies the cached device-power projection drawn by OSD levels above 1.</summary>
    /// <param name="status">Current sustained limit and AutoTDP activity.</param>
    void ApplyOsdPowerStatus(RtssOsdPowerStatus status);

    /// <summary>Reads what RTSS's sensor provider currently publishes.</summary>
    /// <returns>The latest sample, or an empty one when nothing is published.</returns>
    /// <remarks>
    ///     One source for the whole session. The OSD draws from it and AutoTDP classifies stalls with
    ///     it, and sharing keeps that to a single mapping handle, a single cached read per second, and
    ///     a single attempt to start the provider.
    /// </remarks>
    RtssOsdMetrics SampleSensors();

    /// <summary>Whether RTSS already holds a profile with this exact name.</summary>
    /// <param name="rtssProfileName">The application profile name; empty means the global profile.</param>
    /// <remarks>
    ///     Saving an RTSS profile that does not exist creates it, so the service asks first: a
    ///     per-application profile is only written when the user opted the application in or RTSS
    ///     already carries one whose explicit values would otherwise override the global write.
    /// </remarks>
    /// <returns>True for the global profile or an existing named profile.</returns>
    bool ProfileExists(string rtssProfileName);

    /// <summary>Discovers the installation and prepares its profile API when available.</summary>
    /// <param name="cancellationToken">Cancels admission and cooperative discovery work.</param>
    /// <returns>Current availability, identity, and verified bounds.</returns>
    Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Reads the profile after verifying the expected RTSS identity.</summary>
    /// <param name="rtssProfileName">Executable profile name, or empty for global.</param>
    /// <param name="generation">Expected generation from a ready probe.</param>
    /// <param name="cancellationToken">Cancels admission; an in-progress native call may finish.</param>
    /// <returns>The observed profile values and timestamp.</returns>
    Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken);

    /// <summary>Validates and writes one control without requiring readback.</summary>
    /// <param name="request">Profile, value, and expected generation.</param>
    /// <param name="cancellationToken">Cancels admission; an in-progress native call may finish.</param>
    /// <returns>The mutation result; native or identity failures may throw.</returns>
    Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken);
}
