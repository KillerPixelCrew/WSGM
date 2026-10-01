using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class PluginCapabilityChannelTests
{
    [Fact]
    public void AnUndeclaredRoleAndAStaleGenerationAreRefused()
    {
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle],
            new FakeCapabilityPlugin(GpuInstance.PluginId));
        channel.BeginCycle(1);

        Assert.Throws<InvalidOperationException>(() => channel.PublishDescriptorsAsync(
            Set(1, Toggle(CapabilityProfileScope.Switched) with { Role = CapabilityRole.FanDuty }),
            CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => channel.PublishDescriptorsAsync(
            Set(1, Toggle(CapabilityProfileScope.Switched)) with { CycleGeneration = 2 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.True(channel.PublishDescriptorsAsync(Set(1, Toggle(CapabilityProfileScope.Switched)),
            CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void ANewCycleMustIncreaseAndReachesItsSubscribers()
    {
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle],
            new FakeCapabilityPlugin(GpuInstance.PluginId));
        List<long> cycles = [];
        channel.CycleStarted += cycles.Add;

        channel.BeginCycle(1);
        channel.BeginCycle(2);

        Assert.Throws<InvalidOperationException>(() => channel.BeginCycle(2));
        Assert.Equal([1, 2], cycles);
        Assert.Equal(2, channel.CycleGeneration);
    }

    [Fact]
    public async Task CommandsAreAdmittedOnlyInsideAnOpenCycle()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle], plugin);
        channel.BeginCycle(1);
        var open = await channel.ExecuteCommandAsync(Command(1), CancellationToken.None);
        var stale = await channel.ExecuteCommandAsync(Command(2), CancellationToken.None);
        channel.Suspend();
        var suspended = await channel.ExecuteCommandAsync(Command(1), CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, open.Immediate.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, stale.Immediate.Reason?.Code);
        Assert.Equal(CapabilityReasonCode.Quiescing, suspended.Immediate.Reason?.Code);
        Assert.Equal(1, plugin.Commands);
        Assert.Null(await channel.SyncApplicationProfilesAsync(new ApplicationProfileSync(1, 1, []),
            TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ASyncReachesThePluginWhileTheCycleIsOpen()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle], plugin);
        channel.BeginCycle(1);

        var result = await channel.SyncApplicationProfilesAsync(new ApplicationProfileSync(1, 1, []),
            TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(plugin.Syncs);
    }
}
