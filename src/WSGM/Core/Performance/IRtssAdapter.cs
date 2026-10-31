using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Adapter boundary used by the shared service and deterministic tests.</summary>
internal interface IRtssAdapter : IAsyncDisposable
{
    /// <summary>Clears WSGM's OSD without changing RTSS profiles or other applications' displays.</summary>
    void ClearOverlay();

    /// <summary>
    ///     Applies the Custom overlay's configuration (selector level 4). A cheap handoff
    ///     to the adapter's renderer; adapters without one ignore it.
    /// </summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    void ApplyOsdCustomization(RtssOsdCustomSettings settings);

    /// <summary>Applies the cached device-power projection drawn by OSD levels above 1.</summary>
    /// <param name="status">Current sustained limit and AutoTDP activity.</param>
    void ApplyOsdPowerStatus(RtssOsdPowerStatus status);

    /// <summary>Reads the session's current sensor sample.</summary>
    /// <returns>The latest sample, or an empty one when no sensor is available.</returns>
    /// <remarks>
    ///     One source for the whole session. The OSD draws from it and AutoTDP classifies stalls with
    ///     it, and sharing keeps that to a single LibreHardwareMonitor instance and a single cached
    ///     read per second.
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

    /// <summary>Reads the profile from the RTSS instance the preceding ready probe found.</summary>
    /// <param name="rtssProfileName">Executable profile name, or empty for global.</param>
    /// <param name="cancellationToken">Cancels admission; an in-progress native call may finish.</param>
    /// <returns>The observed profile values and timestamp.</returns>
    Task<RtssReadback> ReadAsync(
        string rtssProfileName,
        CancellationToken cancellationToken);

    /// <summary>Validates and writes one control without requiring readback.</summary>
    /// <param name="request">Profile, control, and value.</param>
    /// <param name="cancellationToken">Cancels admission; an in-progress native call may finish.</param>
    /// <returns>The mutation result; native failures may throw.</returns>
    Task<RtssApplyResult> ApplyAsync(
        RtssApplyRequest request,
        CancellationToken cancellationToken);
}
