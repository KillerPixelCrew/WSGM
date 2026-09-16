using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests.Builders;

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
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1)
        };
    }
}
