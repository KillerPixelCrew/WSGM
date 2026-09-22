using System.Collections.Generic;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Admits a bounded number of lighting restores per desired value and device cycle.</summary>
/// <remarks>
///     Lighting is restored from readiness publications, which arrive every few seconds, so this is
///     what keeps a device whose readback never matches from being rewritten forever. It was one
///     attempt per zone per cycle, recorded before the command ran, and a zone whose command the device
///     refused while it was busy right after wake then stayed at its firmware default for the whole
///     cycle. A refused command wrote nothing, so it may be tried again; an uncertain one only after a
///     newer readback shows the zone does not hold the value (<see cref="DeviceDesiredWriteAdmission" />).
///     Either way at most <see cref="MaxAttempts" /> per zone, value and cycle.
/// </remarks>
internal sealed class DeviceLightingRestore
{
    /// <summary>Attempts allowed per zone, desired value and cycle.</summary>
    internal const int MaxAttempts = 3;

    private readonly Dictionary<DeviceCapabilityKey, Attempt> _attempts = [];
    private readonly Lock _gate = new();

    internal static bool IsLighting(CapabilityRole role)
    {
        return role is
            CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
            or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
            or CapabilityRole.LightingEffectSpeed;
    }

    internal bool CanApply(DeviceCapabilityView view)
    {
        lock (_gate)
        {
            return CanApplyUnderGate(view);
        }
    }

    /// <summary>Claims one restore attempt.</summary>
    /// <param name="view">The capability and its desired and observed values.</param>
    /// <returns>The attempt number, or zero when no restore is admitted now.</returns>
    internal int TryBegin(DeviceCapabilityView view)
    {
        lock (_gate)
        {
            if (!CanApplyUnderGate(view))
            {
                return 0;
            }

            var key = Key(view);
            var count = _attempts.TryGetValue(key, out var previous) ? previous.Count + 1 : 1;
            _attempts[key] = new Attempt(view.Projection.State.CycleGeneration, view.Projection.DesiredValue!, count,
                true, false);
            return count;
        }
    }

    /// <summary>Records how a claimed attempt ended.</summary>
    /// <param name="view">The capability the attempt was claimed for.</param>
    /// <param name="outcome">What the device reported.</param>
    internal void Complete(DeviceCapabilityView view, CommandOutcome outcome)
    {
        lock (_gate)
        {
            var key = Key(view);
            if (_attempts.TryGetValue(key, out var attempt))
            {
                _attempts[key] = attempt with { InFlight = false, Done = outcome.IsApplied() };
            }
        }
    }

    private static DeviceCapabilityKey Key(DeviceCapabilityView view)
    {
        return new DeviceCapabilityKey(view.Descriptor.CapabilityId, view.Descriptor.InstanceId);
    }

    private bool CanApplyUnderGate(DeviceCapabilityView view)
    {
        var projection = view.Projection;
        var key = Key(view);
        // A profile may change to a value the firmware already holds. Observe that transition
        // too, so returning to the previous profile is a new restore opportunity.
        if (_attempts.TryGetValue(key, out var attempt)
            && (attempt.Cycle != projection.State.CycleGeneration
                || projection.DesiredValue is not { } current
                || !DeviceCoordinator.SameValue(attempt.Value, current)))
        {
            _attempts.Remove(key);
        }

        if (!IsLighting(view.Descriptor.Role) || !DeviceDesiredWriteAdmission.TryAdmit(view).Admitted)
        {
            return false;
        }

        return !_attempts.TryGetValue(key, out var existing)
               || (!existing.InFlight && !existing.Done && existing.Count < MaxAttempts);
    }

    private readonly record struct Attempt(long Cycle, CapabilityValue Value, int Count, bool InFlight, bool Done);
}
