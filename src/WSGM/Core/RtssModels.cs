using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Availability of the optional external RTSS integration.</summary>
internal enum RtssAvailability
{
    Unknown,
    NotInstalled,
    Incompatible,
    NotRunning,
    AdapterUnavailable,
    Ready,
    Degraded
}

/// <summary>Bounds reported by a concrete RTSS adapter.</summary>
internal sealed record RtssCapabilities(
    int MinimumFrameLimit,
    int MaximumFrameLimit,
    IReadOnlySet<int> OverlayLevels)
{
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
internal sealed record RtssProbe(
    RtssAvailability Availability,
    string? Version,
    string? ExecutablePath,
    long Generation,
    RtssCapabilities? Capabilities,
    string? Diagnostic,
    int? ProcessId = null);

/// <summary>Result of querying the active global or application profile.</summary>
internal sealed record RtssReadback(PerformanceValues Values, DateTimeOffset Timestamp);

/// <summary>Narrow property update sent to the adapter.</summary>
internal sealed record RtssApplyRequest(
    string RtssProfileName,
    PerformanceControl Control,
    int Value,
    long Generation);

/// <summary>Result of an adapter mutation attempt.</summary>
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
    ///     Without this check every focused executable grew a profile
    ///     (device-observed 2026-09-02).
    /// </remarks>
    bool ProfileExists(string rtssProfileName);

    Task<RtssProbe> ProbeAsync(CancellationToken cancellationToken);

    Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        long generation,
        CancellationToken cancellationToken);

    Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken);
}
