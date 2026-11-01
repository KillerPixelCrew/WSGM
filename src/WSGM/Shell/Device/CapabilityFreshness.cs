using System;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>How long a published capability state stays current, and whether it may be commanded.</summary>
internal static class CapabilityFreshness
{
    /// <summary>Whether a command may be issued against this state.</summary>
    /// <remarks>
    ///     Readback is not a precondition: many firmwares cannot report what they were set to, and a
    ///     capability that was never read (<see cref="HardwareStateQuality.Unknown" />) is still
    ///     writable while the plugin reports it available. An expired or faulted observation is not.
    /// </remarks>
    /// <param name="state">Published availability and evidence quality after freshness projection.</param>
    /// <returns>True when available and neither stale nor faulted; unknown readback alone does not disable writes.</returns>
    internal static bool CanCommand(CapabilityState state)
    {
        return state is
        {
            Available: true,
            Quality: not (HardwareStateQuality.Stale or HardwareStateQuality.Faulted)
        };
    }

    /// <summary>
    ///     Returns the state as it should be presented now, downgrading it to
    ///     <see cref="HardwareStateQuality.Stale" /> when it can no longer be trusted.
    /// </summary>
    /// <param name="state">The published state.</param>
    /// <param name="role">The capability's role, which decides how long a reading stays current.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The state, or its stale form once a live reading aged out.</returns>
    internal static CapabilityState Evaluate(
        CapabilityState state,
        CapabilityRole role,
        DateTimeOffset now)
    {
        // A faulted capability is already saying something stronger than "old". Downgrading it to
        // stale would lose the fault.
        if (state.Quality is HardwareStateQuality.Faulted or HardwareStateQuality.Unknown)
        {
            return state;
        }

        var maxAge = MaximumAge(role);
        if (maxAge is { } limit && (state.ObservedAt is not { } observedAt || now - observedAt > limit))
        {
            return state with
            {
                Quality = HardwareStateQuality.Stale,
                Available = false,
                Reason = new CapabilityReason(CapabilityReasonCode.ObservationExpired,
                    $"Observation is older than {maxAge}.", true)
            };
        }

        return state;
    }

    /// <summary>How long a live reading stays current, or null for a value that never expires.</summary>
    /// <remarks>
    ///     Only a live measurement such as a fan RPM or a temperature ages out. A setting is whatever was
    ///     last written or read, however long ago, as HC treats it: expiring it made every write and
    ///     control wait for a fresh readback, which a device like the Ally never delivers.
    /// </remarks>
    private static TimeSpan? MaximumAge(CapabilityRole role)
    {
        return role is CapabilityRole.Telemetry or CapabilityRole.FanMeasuredRpm ? TimeSpan.FromSeconds(5) : null;
    }
}
