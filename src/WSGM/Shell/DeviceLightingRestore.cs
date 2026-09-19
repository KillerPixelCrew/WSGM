using System.Collections.Generic;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Admits one lighting restore per desired value and device cycle.</summary>
/// <remarks>Readback can make a first restore ready, but cannot request repeated firmware writes.</remarks>
internal sealed class DeviceLightingRestore
{
    private readonly Dictionary<DeviceCapabilityKey, (long Cycle, CapabilityValue Value)> _attempts = [];
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

    internal bool TryBegin(DeviceCapabilityView view)
    {
        lock (_gate)
        {
            if (!CanApplyUnderGate(view))
            {
                return false;
            }

            _attempts[new DeviceCapabilityKey(view.Descriptor.CapabilityId, view.Descriptor.InstanceId)] =
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

        var admission = DeviceDesiredWriteAdmission.TryAdmit(view);
        if (!IsLighting(view.Descriptor.Role) || !admission.Admitted)
        {
            return false;
        }

        var desired = admission.DesiredValue!;

        return !_attempts.TryGetValue(new DeviceCapabilityKey(view.Descriptor.CapabilityId, view.Descriptor.InstanceId),
                   out var previous)
               || previous.Cycle != projection.State.CycleGeneration ||
               !DeviceCoordinator.SameValue(previous.Value, desired);
    }
}
