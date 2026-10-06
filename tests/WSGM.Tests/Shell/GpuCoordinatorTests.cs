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

    [Fact]
    public async Task SeparateCoordinatorsStartTheirSyncRevisionsIndependently()
    {
        FakeCapabilityPlugin firstPlugin = new(GpuInstance.PluginId);
        FakeCapabilityPlugin secondPlugin = new(GpuInstance.PluginId);
        var firstProfiles = Profiles();
        var secondProfiles = Profiles();
        await using var first = Coordinator(firstProfiles);
        await using var second = Coordinator(secondProfiles);
        var firstChannel = first.Open(GpuInstance, Manifest(), firstPlugin);
        var secondChannel = second.Open(GpuInstance, Manifest(), secondPlugin);
        await PublishAsync(firstChannel, Toggle(CapabilityProfileScope.NativePerApplication));
        await first.ApplyProfilesAsync(firstProfiles.Current, CancellationToken.None);
        await PublishAsync(secondChannel, Toggle(CapabilityProfileScope.NativePerApplication));
        await second.ApplyProfilesAsync(secondProfiles.Current, CancellationToken.None);

        Assert.Equal(Assert.Single(firstPlugin.Syncs).Revision, Assert.Single(secondPlugin.Syncs).Revision);
    }

    [Fact]
    public async Task DisposalJoinsAnInFlightRefreshEvenAfterItsPublisherWasClosed()
    {
        var profiles = Profiles();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        await using GpuCoordinator coordinator = new(action => action(), profiles,
            new PluginHost(action => action(), new MemoryPluginConfigurationStore()), () =>
            {
                if (Volatile.Read(ref block))
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }

                return true;
            });
        var channel = coordinator.Open(GpuInstance, Manifest(), new FakeCapabilityPlugin(GpuInstance.PluginId));
        await PublishAsync(channel, Toggle(CapabilityProfileScope.NativePerApplication));
        await coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None);
        Volatile.Write(ref block, true);
        var refresh = Task.Run(() => coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None));
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.Close(channel);
            disposal = coordinator.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Admission closure cancels an admitted refresh; its owner still joins its completion.
            }

            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task ClosingAdmissionRefusesCommandsAndNewPublishersUntilOrderedDisposal()
    {
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        await using var coordinator = Coordinator(Profiles());
        var channel = coordinator.Open(GpuInstance, Manifest(), plugin);
        await PublishAsync(channel, Toggle(CapabilityProfileScope.GlobalOnly));

        coordinator.CloseAdmission();
        coordinator.CloseAdmission();
        var refused = await coordinator.ExecuteAsync(GpuInstance.PluginId, "graphics.toggle", null, Flag(true));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Equal(0, plugin.Commands);
        Assert.Single(coordinator.Publishers());
        Assert.False(channel.IsClosed);
        Assert.Throws<InvalidOperationException>(() => coordinator.Open(
            new PluginInstanceIdentity(GpuInstance.PluginId, "other"), Manifest(), plugin));

        await coordinator.DisposeAsync();
        Assert.True(channel.IsClosed);
        Assert.Empty(coordinator.Publishers());
    }

    [Fact]
    public async Task ClosingAdmissionRefusesARefreshQueuedBehindAnActiveLane()
    {
        var profiles = Profiles();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var reads = 0;
        await using GpuCoordinator coordinator = new(action => action(), profiles,
            new PluginHost(action => action(), new MemoryPluginConfigurationStore()), () =>
            {
                Interlocked.Increment(ref reads);
                if (Volatile.Read(ref block))
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }

                return true;
            });
        FakeCapabilityPlugin plugin = new(GpuInstance.PluginId);
        var channel = coordinator.Open(GpuInstance, Manifest(), plugin);
        await PublishAsync(channel, Toggle(CapabilityProfileScope.NativePerApplication), false);
        await coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None);
        var syncs = plugin.Syncs.Count;
        Volatile.Write(ref block, true);
        var active = Task.Run(() => coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None));
        var queued = Task.CompletedTask;
        var admittedReads = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = coordinator.ApplyProfilesAsync(profiles.Current, CancellationToken.None);
            Assert.False(queued.IsCompleted);
            admittedReads = Volatile.Read(ref reads);
            coordinator.CloseAdmission();
        }
        finally
        {
            release.TrySetResult();
            await CompleteClosedWorkAsync(active);
            await CompleteClosedWorkAsync(queued);
        }

        Assert.Equal(admittedReads, Volatile.Read(ref reads));
        Assert.Equal(syncs, plugin.Syncs.Count);
        Assert.Equal(0, plugin.Commands);

        static async Task CompleteClosedWorkAsync(Task work)
        {
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Closed work either observes cancellation or returns before acquiring native state.
            }
        }
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
