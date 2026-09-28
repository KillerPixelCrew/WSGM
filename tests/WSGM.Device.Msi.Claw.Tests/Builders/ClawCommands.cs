using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Msi.Claw.Tests.Builders;

/// <summary>Capability commands addressed to the fake hardware's cycle.</summary>
internal static class ClawCommands
{
    internal const long CycleGeneration = 7;

    internal static CapabilityCommand Command(
        string capabilityId,
        string? instanceId,
        CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            RequestedValue = value,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = CycleGeneration,
            Deadline = Deadline.After(TimeSpan.FromMinutes(1))
        };
    }
}
