using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Fakes;
using static WSGM.Tests.Builders.CapabilityBuilders;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

/// <summary>The graphics publishers' channels, their cycle restore and their per-application sync.</summary>
public sealed class GpuCoordinatorTests
{
    private static PluginManifest Manifest()
    {
        return new PluginManifest
        {
            Id = GpuInstance.PluginId,
            Name = "Test graphics",
            Version = "1.0.0",
            Category = PluginCategories.Gpu,
            EntryAssembly = "Fixture.dll",
            EntryType = "Fixture.Plugin",
            Capabilities = [CapabilityRole.GenericToggle]
        };
    }

    private static GpuCoordinator Coordinator(ProfileService profiles)
    {
        return new GpuCoordinator(action => action(), profiles,
            new PluginHost(action => action(), new MemoryPluginConfigurationStore()), () => true);
    }

    /// <summary>Starts a cycle and publishes one descriptor, optionally with its first state.</summary>
    private static async Task PublishAsync(PluginCapabilityChannel channel, CapabilityDescriptor descriptor,
        bool withState = true)
    {
        channel.BeginCycle(channel.CycleGeneration + 1);
        await channel.PublishDescriptorsAsync(Set(1, descriptor) with { CycleGeneration = channel.CycleGeneration },
            CancellationToken.None);
        if (withState)
        {
            await channel.PublishCapabilityStateAsync(
                State(1, Flag(false)) with { CycleGeneration = channel.CycleGeneration }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheCycleRestoreWaitsForTheFirstStatesInsteadOfSkippingEveryCapability()
    {
        var profiles = Profiles();
        Assert.True(await CapabilityUserWrites.PersistAsync(profiles, GpuPublisher,
            View(Toggle(CapabilityProfileScope.GlobalOnly), null, null), Flag(true), CancellationToken.None));
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        await using var coordinator = Coordinator(profiles);
        var channel = coordinator.Open(GpuInstance, Manifest(), plugin);

        await PublishAsync(channel, Toggle(CapabilityProfileScope.GlobalOnly), false);
        Assert.Equal(0, plugin.Commands);

        await channel.PublishCapabilityStateAsync(State(1, Flag(false)), CancellationToken.None);

        await AsyncConditions.WaitForAsync(() => plugin.Commands == 1);
    }

    [Fact]
    public async Task SyncRevisionsKeepRisingWhenThePluginIsStartedAgain()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        await using var coordinator = Coordinator(Profiles());
        var first = coordinator.Open(GpuInstance, Manifest(), plugin);
        await PublishAsync(first, Toggle(CapabilityProfileScope.NativePerApplication));
        await AsyncConditions.WaitForAsync(() => plugin.Syncs.Count == 1);

        coordinator.Close(first);
        var second = coordinator.Open(GpuInstance, Manifest(), plugin);
        await PublishAsync(second, Toggle(CapabilityProfileScope.NativePerApplication));
        await AsyncConditions.WaitForAsync(() => plugin.Syncs.Count == 2);

        Assert.True(plugin.Syncs[1].Revision > plugin.Syncs[0].Revision);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task OnlyASyncThePluginAppliedInFullIsSkippedWhenNothingChanged(bool refused, int syncs)
    {
        var profiles = Profiles();
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId) { RefuseSyncs = refused };
        await using var coordinator = Coordinator(profiles);
        var channel = coordinator.Open(GpuInstance, Manifest(), plugin);
        await PublishAsync(channel, Toggle(CapabilityProfileScope.NativePerApplication));
        await AsyncConditions.WaitForAsync(() => plugin.Syncs.Count == 1);

        await coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None);

        Assert.Equal(syncs, plugin.Syncs.Count);
    }

    [Fact]
    public async Task ARegistrationWhoseChannelEndedDoesNotBlockTheInstanceFromOpeningAgain()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        await using var coordinator = Coordinator(Profiles());
        var live = coordinator.Open(GpuInstance, Manifest(), plugin);

        Assert.Throws<InvalidOperationException>(() => coordinator.Open(GpuInstance, Manifest(), plugin));

        live.Dispose();
        var reopened = coordinator.Open(GpuInstance, Manifest(), plugin);

        Assert.False(reopened.IsClosed);
        Assert.Single(coordinator.Publishers());
    }
}
