using System.Collections.Concurrent;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using static WSGM.Tests.PluginBuilders;

namespace WSGM.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public async Task StateReadbackKeepsOriginAndDropsReorderedOrInvalidValues()
    {
        ConcurrentQueue<Action> ui = new();
        PluginHost host = new(ui.Enqueue);
        var instance = Admit(host, new FakePlugin("test.ir", publishReadyOnResume: true), "one");
        await instance.StartAsync(Deadline, default);
        List<PluginStatePublication> observed = [];
        host.StateChanged += observed.Add;
        var first = new PluginStatePublication(instance.Identity, 1, 1, "level", new(Number: 40), PluginStateOrigin.HardwareReadback);
        instance.PublishState(first);
        var latest = first with { Sequence = 3, Value = new(Number: 50), ConfigurationRevision = 7 };
        instance.PublishState(latest);
        instance.PublishState(first with { Sequence = 2 });
        instance.PublishState(first with { Sequence = 4, Value = new(Number: double.NaN) });
        while (ui.TryDequeue(out var action)) { action(); }
        Assert.Equal(latest, Assert.Single(observed));
        Assert.Equal(latest, Assert.Single(host.StateSnapshot(instance.Identity)));
        await Close(instance);
    }

    [Fact]
    public async Task AResumeRetiresStateAndAllowsTheNewGenerationToRestartItsSequence()
    {
        PluginHost host = new(action => action());
        var instance = Admit(host, new FakePlugin("test.ir", publishReadyOnResume: true), "one");
        await instance.StartAsync(Deadline, default);
        var state = new PluginStatePublication(instance.Identity, 1, 100, "ready", new(Boolean: true), PluginStateOrigin.Initialization);
        instance.PublishState(state);
        await instance.ResumeAsync(2, Deadline, default);
        Assert.Empty(host.StateSnapshot(instance.Identity));
        instance.PublishState(state with { Sequence = 101 });
        Assert.Empty(host.StateSnapshot(instance.Identity));
        var resumed = state with { Generation = 2, Sequence = 1 };
        instance.PublishState(resumed);
        Assert.Equal(resumed, Assert.Single(host.StateSnapshot(instance.Identity)));
        await Close(instance);
    }

    [Fact]
    public async Task StateKeysAreBoundedAndRetiredRegistrationsCannotPublishIntoReplacements()
    {
        PluginHost host = new(action => action());
        var instance = Admit(host, new FakePlugin("test.ir", publishReadyOnResume: true), "one");
        await instance.StartAsync(Deadline, default);
        var state = new PluginStatePublication(instance.Identity, 1, 1, "ready", new(Boolean: true), PluginStateOrigin.Initialization);
        for (int index = 1; index <= 129; index++)
        { instance.PublishState(state with { Sequence = index, Key = "state" + index }); }
        Assert.Equal(128, host.StateSnapshot(instance.Identity).Length);
        await Close(instance);
        var replacement = Admit(host, new FakePlugin("test.ir", publishReadyOnResume: true), "one");
        await replacement.StartAsync(Deadline, default);
        instance.PublishState(state with { Sequence = 130 });
        Assert.Empty(host.StateSnapshot(instance.Identity));
        await Close(replacement);
    }

    [Fact]
    public async Task IndependentInstancesWorkWithoutDeviceAndCoexistWithItsSingleton()
    {
        PluginHost host = new(action => action());
        FakePlugin first = new("test.ir", publishReadyOnResume: true);
        FakePlugin second = new("test.ir", publishReadyOnResume: true);
        var one = Admit(host, first, "one");
        var two = Admit(host, second, "two");
        await one.StartAsync(Deadline, default);
        await two.StartAsync(Deadline, default);
        Assert.Equal(2, host.Snapshot().Length);
        FakePlugin device = new("test.device", publishReadyOnResume: true);
        var deviceInstance = host.Admit(device, new(device.Id, "device"), PluginCategories.Device,
            PluginCategoryPolicy.Device, true, 1, "fixture-state");
        Assert.Throws<InvalidOperationException>(() => host.Admit(new FakePlugin("another.device", publishReadyOnResume: true),
            new("another.device", "device"), PluginCategories.Device, PluginCategoryPolicy.Device, true, 1, "fixture-state"));
        await deviceInstance.StartAsync(Deadline, default);
        await host.SetModeAsync(PluginSessionMode.Game, Deadline, default);
        Assert.Equal(PluginSessionMode.Game, first.Mode);
        Assert.Equal(PluginSessionMode.Game, second.Mode);
        Assert.Equal(PluginSessionMode.Game, device.Mode);
        await Close(one);
        await Close(two);
        await Close(deviceInstance);
        Assert.Empty(host.Snapshot());
    }

    [Fact]
    public async Task ResumeDropsOldGenerationsAndQueuedUiPublications()
    {
        ConcurrentQueue<Action> ui = new();
        PluginHost host = new(ui.Enqueue);
        FakePlugin plugin = new("test.ir", publishReadyOnResume: true);
        var instance = Admit(host, plugin, "one");
        List<PluginHealthPublication> observed = [];
        host.HealthChanged += observed.Add;
        await instance.StartAsync(Deadline, default);
        await instance.SuspendAsync(Deadline, default);
        await instance.ResumeAsync(2, Deadline, default);
        plugin.Host!.PublishHealth(new(instance.Identity, 1, PluginHealth.Failed, "stale"));
        plugin.Host.PublishHealth(new(new("other.plugin", "one"), 2, PluginHealth.Failed, "wrong identity"));
        while (ui.TryDequeue(out var action)) { action(); }
        Assert.NotEmpty(observed);
        Assert.All(observed, publication => Assert.Equal(2, publication.Generation));
        Assert.Equal(PluginHealth.Ready, Assert.Single(host.Snapshot()).Health);
        await Close(instance);
    }

    [Fact]
    public async Task UnconfirmedStopIsNotRetriedAndContinuesReservingTheSlot()
    {
        PluginHost host = new(action => action());
        FakePlugin plugin = new("test.ir", publishReadyOnResume: true) { Released = false };
        var instance = Admit(host, plugin, "one");
        await instance.StartAsync(Deadline, default);
        Assert.False(await instance.StopAsync(Deadline, default));
        Assert.False(await instance.StopAsync(Deadline, default));
        await instance.DisposeAsync();
        Assert.Equal(1, plugin.Stops);
        Assert.Equal(1, plugin.Disposals);
        Assert.Throws<InvalidOperationException>(() => Admit(host, new FakePlugin("test.ir", publishReadyOnResume: true), "one"));
    }

    [Fact]
    public async Task CanceledWaitCannotOvertakeAnUncooperativeStartOrPublishLateReady()
    {
        PluginHost host = new(action => action());
        ConcurrentQueue<PluginHealthPublication> publications = new();
        host.HealthChanged += publications.Enqueue;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlugin plugin = new("test.ir", publishReadyOnResume: true) { StartWork = async () => { entered.SetResult(); await release.Task; } };
        var instance = Admit(host, plugin, "one");
        using CancellationTokenSource cancellation = new();
        var start = instance.StartAsync(Deadline, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        using CancellationTokenSource stopCancellation = new();
        var stop = instance.StopAsync(Deadline, stopCancellation.Token);
        stopCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.Equal(0, plugin.Stops);
        Assert.Equal(0, plugin.Disposals);
        release.SetResult();
        // This cleanup queues behind the still-owned startup lane and waits for actual completion.
        await Close(instance);
        Assert.Equal(1, plugin.Stops);
        Assert.Equal(1, plugin.Disposals);
        Assert.Empty(host.Snapshot());
        Assert.DoesNotContain(publications, publication => publication.Health == PluginHealth.Ready);
    }

    [Fact]
    public async Task AnObsoleteModeRevisionCannotReplaceNewerIntent()
    {
        PluginHost host = new(action => action());
        FakePlugin plugin = new("test.ir", publishReadyOnResume: true);
        var instance = Admit(host, plugin, "one");
        await instance.StartAsync(Deadline, default);
        await instance.SessionChangedAsync(PluginSessionMode.Game, 2, Deadline, default);
        await instance.SessionChangedAsync(PluginSessionMode.Desktop, 1, Deadline, default);
        Assert.Equal(PluginSessionMode.Game, plugin.Mode);
        await Close(instance);
    }

    [Fact]
    public async Task NewModeIntentCancelsThePreviousCooperativeOperation()
    {
        PluginHost host = new(action => action());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlugin plugin = new("test.ir", publishReadyOnResume: true)
        {
            ModeWork = async (mode, token) =>
            {
                if (mode != PluginSessionMode.Game) { return; }
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var instance = Admit(host, plugin, "one");
        await instance.StartAsync(Deadline, default);
        var obsolete = host.SetModeAsync(PluginSessionMode.Game, Deadline, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.SetModeAsync(PluginSessionMode.Desktop, Deadline, default);
        await obsolete;
        Assert.Equal(PluginSessionMode.Desktop, plugin.Mode);
        Assert.False(instance.Quarantined);
        await Close(instance);
    }
}
