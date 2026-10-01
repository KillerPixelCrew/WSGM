using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Fakes;

/// <summary>A capability publisher the test drives by hand; every command applies and reads back.</summary>
/// <param name="roles">The roles its manifest declares.</param>
internal sealed class FakeCapabilityPublisher(params CapabilityRole[] roles) : ICapabilityPublisher
{
    internal List<CapabilityCommand> Commands { get; } = [];

    public long CycleGeneration => 1;

    public IReadOnlyList<CapabilityRole> DeclaredCapabilities { get; } = roles;

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;

    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public Task<DeviceCommandDispatch> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(new DeviceCommandDispatch(new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            ReadbackValue = command.RequestedValue,
            CompletedAt = DateTimeOffset.UtcNow
        }));
    }

    internal void Publish(CapabilityDescriptorSet set)
    {
        DescriptorSetReceived?.Invoke(set);
    }

    internal void PublishState(long sequence, CapabilityState state)
    {
        CapabilityStateReceived?.Invoke(new CapabilityStateDelta(sequence, state));
    }
}
