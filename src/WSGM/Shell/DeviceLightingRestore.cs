using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Admits one lighting restore per desired value and device cycle.</summary>
/// <remarks>Readback can make a first restore ready, but cannot request repeated firmware writes.</remarks>
internal sealed class DeviceLightingRestore
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceCapabilityKey, (long Cycle, CapabilityValue Value)> _attempts = [];

    internal static bool IsLighting(CapabilityRole role) => role is
        CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
        or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
        or CapabilityRole.LightingEffectSpeed;

    internal bool CanApply(DeviceCapabilityView view)
    {
        lock (_gate)
        {
            return CanApplyUnderGate(view);
        }
    }

    internal bool TryBegin(DeviceCapabilityView view)
    {
        lock (_gate)
        {
            if (!CanApplyUnderGate(view))
            {
                return false;
            }

            _attempts[new(view.Descriptor.CapabilityId, view.Descriptor.InstanceId)] =
                (view.Projection.State.CycleGeneration, view.Projection.DesiredValue!);
            return true;
        }
    }

    private bool CanApplyUnderGate(DeviceCapabilityView view)
    {
        var projection = view.Projection;
        DeviceCapabilityKey key = new(view.Descriptor.CapabilityId, view.Descriptor.InstanceId);
        // A profile may change to a value the firmware already holds. Observe that transition
        // too, so returning to the previous profile is a new restore opportunity.
        if (_attempts.TryGetValue(key, out var attempt)
            && (attempt.Cycle != projection.State.CycleGeneration
                || projection.DesiredValue is not { } current
                || !DeviceCoordinator.SameValue(attempt.Value, current)))
        {
            _attempts.Remove(key);
        }

        if (!IsLighting(view.Descriptor.Role) || !view.Descriptor.SupportsWrite
            || !projection.State.Available || projection.DesiredValueOutOfRange
            || projection.State.Quality is not (HardwareStateQuality.Observed or HardwareStateQuality.Verified)
            || projection.PendingValue is not null
            || view.LastResult?.Outcome is CommandOutcome.Indeterminate or CommandOutcome.TimedOut
            || projection.DesiredValue is not { } desired
            || (projection.State.ObservedValue is { } observed && DeviceCoordinator.SameValue(observed, desired)))
        {
            return false;
        }

        return !_attempts.TryGetValue(new(view.Descriptor.CapabilityId, view.Descriptor.InstanceId), out var previous)
            || previous.Cycle != projection.State.CycleGeneration || !DeviceCoordinator.SameValue(previous.Value, desired);
    }
}
