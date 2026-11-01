using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The primary power limit AutoTDP controls and its paired limit, as the device currently publishes them.</summary>
/// <remarks>
///     Looked up afresh on every call, because a device cycle replaces the capability views. Nothing is
///     cached here, so a limit that disappears mid-session simply reads as absent on the next tick.
/// </remarks>
internal sealed class AutoTdpPowerCapability
{
    private readonly Func<IReadOnlyList<DeviceCapabilityView>> _capabilities;

    /// <summary>Creates the lookup over the device's published capabilities.</summary>
    /// <param name="capabilities">The current capability views.</param>
    internal AutoTdpPowerCapability(Func<IReadOnlyList<DeviceCapabilityView>> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
    }

    /// <summary>Whether the limit can be commanded in this cycle.</summary>
    /// <param name="view">The capability, or null when it is absent.</param>
    /// <returns>True when the capability exists and accepts a command now.</returns>
    /// <remarks>
    ///     No readback, no settled uncertain result and no idle command lane: HC writes the limit and moves
    ///     on, and waiting for any of those kept AutoTDP unavailable on the Ally for a whole session.
    /// </remarks>
    internal static bool CanWrite(DeviceCapabilityView? view)
    {
        return view is not null && CapabilityFreshness.CanCommand(view.Projection.State);
    }

    /// <summary>The limit as last read or written, else the value the profile asks for.</summary>
    /// <param name="view">The capability, or null.</param>
    /// <returns>The watts, or null when neither is known.</returns>
    internal static int? CurrentWatts(DeviceCapabilityView? view)
    {
        return view?.Projection.State.ObservedValue?.IntegerValue ?? view?.Projection.DesiredValue?.IntegerValue;
    }

    /// <summary>The bounds the plugin declares for the primary limit.</summary>
    /// <param name="power">The primary power-limit capability.</param>
    /// <returns>The bounds, with an absent value read as zero.</returns>
    internal static AutoTdpLimits Limits(DeviceCapabilityView power)
    {
        return new AutoTdpLimits(
            power.Descriptor.Minimum ?? 0,
            power.Descriptor.Maximum ?? 0,
            power.Descriptor.Step ?? 0);
    }

    /// <summary>Finds the writable integer sustained-power capability.</summary>
    /// <returns>The capability, or null when the device publishes none.</returns>
    internal DeviceCapabilityView? FindPrimary()
    {
        return FindPrimary(_capabilities());
    }

    /// <summary>
    ///     The one definition of the primary power limit: the first writable integer sustained-power
    ///     capability. AutoTDP, the manual power path and the per-application restore all use it.
    /// </summary>
    /// <param name="views">The published capability views.</param>
    /// <returns>The capability, or null when none qualifies.</returns>
    internal static DeviceCapabilityView? FindPrimary(IEnumerable<DeviceCapabilityView> views)
    {
        return views.FirstOrDefault(view =>
            view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });
    }

    /// <summary>Finds the limit the primary declares as its pair.</summary>
    /// <param name="primary">The primary power-limit capability.</param>
    /// <returns>The paired capability, or null when none is declared or published.</returns>
    internal DeviceCapabilityView? FindPaired(DeviceCapabilityView primary)
    {
        return primary.Descriptor.PairedPowerLimitId is { } id
            ? _capabilities().FirstOrDefault(view => view.Descriptor.CapabilityId == id &&
                                                     view.Descriptor.InstanceId is null)
            : null;
    }

    /// <summary>Whether AutoTDP may be admitted against this deadline and the current limits.</summary>
    /// <param name="targetFrametimeMs">The deadline control would judge against.</param>
    /// <returns>The shared admission state.</returns>
    internal AutoTdpAvailability Availability(double targetFrametimeMs)
    {
        if (!double.IsFinite(targetFrametimeMs) || targetFrametimeMs <= 0)
        {
            return new AutoTdpAvailability(false, "No frame-rate target is set.", null);
        }

        var power = FindPrimary();
        if (power?.Descriptor.PairedPowerLimitId is not null
            && !CanWrite(FindPaired(power)))
        {
            return new AutoTdpAvailability(false, "The paired power limit is unavailable.", targetFrametimeMs);
        }

        return CanWrite(power)
            ? new AutoTdpAvailability(true, string.Empty, targetFrametimeMs)
            : new AutoTdpAvailability(false, "No primary power limit is available.", targetFrametimeMs);
    }

    /// <summary>Fills the trace columns that describe the limits before a decision is applied.</summary>
    /// <param name="trace">The row.</param>
    /// <param name="power">The primary power-limit capability.</param>
    /// <param name="limits">The bounds the decision used, or null for a release.</param>
    internal void TraceBefore(AutoTdpTraceRow trace, DeviceCapabilityView power, AutoTdpLimits? limits)
    {
        trace.Limits = limits;
        trace.PowerCapability = power.Descriptor.CapabilityId;
        trace.PairedCapability = power.Descriptor.PairedPowerLimitId;
        trace.ObservedWatts = power.Projection.State.ObservedValue?.IntegerValue;
        trace.ObservedQuality = power.Projection.State.Quality.ToString();
        trace.PairedObservedWatts = FindPaired(power)?.Projection.State.ObservedValue?.IntegerValue;
    }

    /// <summary>Fills the trace columns that describe the limits after a write attempt.</summary>
    /// <param name="trace">The row.</param>
    internal void TraceAfter(AutoTdpTraceRow trace)
    {
        if (FindPrimary() is not { } power)
        {
            return;
        }

        trace.PostObservedWatts = power.Projection.State.ObservedValue?.IntegerValue;
        trace.PostPairedObservedWatts = FindPaired(power)?.Projection.State.ObservedValue?.IntegerValue;
    }
}
