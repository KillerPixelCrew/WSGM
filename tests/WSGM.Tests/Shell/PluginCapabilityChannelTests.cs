using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class PluginCapabilityChannelTests
{
    [Fact]
    public void AnUndeclaredRoleIsRefusedWithoutRejectingALaterValidSet()
    {
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle],
            new FakeCapabilityPlugin(GpuInstance.PluginId));
        channel.Open();
        Assert.Throws<InvalidOperationException>(() => channel.PublishDescriptorsAsync(
            Set(1, Toggle(CapabilityProfileScope.Switched) with { Role = CapabilityRole.FanDuty }),
            CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.True(channel.PublishDescriptorsAsync(Set(1, Toggle(CapabilityProfileScope.Switched)),
            CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void SuspendAndReopenPublishTheirAdmissionTransitions()
    {
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle],
            new FakeCapabilityPlugin(GpuInstance.PluginId));
        List<string> calls = [];
        channel.Opened += () => calls.Add("open");
        channel.AdmissionClosed += () => calls.Add("close");
        channel.Open();
        Assert.True(channel.IsActive);
        channel.Suspend();
        Assert.False(channel.IsActive);
        channel.Open();
        Assert.True(channel.IsActive);
        Assert.Equal(["open", "close", "open"], calls);
    }

    [Fact]
    public async Task CommandsAreAdmittedOnlyWhileTheOwnedChannelIsOpen()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle], plugin);
        var closed = await channel.ExecuteCommandAsync(Command(1), CancellationToken.None);
        channel.Open();
        var open = await channel.ExecuteCommandAsync(Command(1), CancellationToken.None);
        channel.Suspend();
        var suspended = await channel.ExecuteCommandAsync(Command(1), CancellationToken.None);
        Assert.Equal(CapabilityReasonCode.Quiescing, closed.Immediate.Reason?.Code);
        Assert.Equal(CommandOutcome.AppliedUnverified, open.Immediate.Outcome);
        Assert.Equal(CapabilityReasonCode.Quiescing, suspended.Immediate.Reason?.Code);
        Assert.Equal(1, plugin.Commands);
        Assert.Null(await channel.SyncApplicationProfilesAsync(new ApplicationProfileSync(1, []),
            TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ASyncReachesThePluginWhileTheChannelIsOpen()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        using PluginCapabilityChannel channel = new(GpuInstance, [CapabilityRole.GenericToggle], plugin);
        channel.Open();
        var result = await channel.SyncApplicationProfilesAsync(new ApplicationProfileSync(1, []),
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Single(plugin.Syncs);
    }
}
