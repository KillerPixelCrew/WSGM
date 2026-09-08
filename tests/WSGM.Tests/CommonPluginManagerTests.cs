using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginManagerTests
{
    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddSeconds(5);

    [Fact]
    public async Task ExplicitInstancesRunWithoutDeviceAndDisableIndependently()
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "test.plugin");
        PluginHost host = new(action => action());
        List<FakePlugin> created = [];
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"), action => action(),
            (package, _) => { FakePlugin plugin = new(package.Manifest.Id); created.Add(plugin); return Task.FromResult<IPlugin>(plugin); });
        var first = new CommonPluginInstanceConfig { PluginId = "test.plugin", InstanceId = "one", Enabled = false };
        await manager.ReconcileAsync([first], default);
        Assert.Empty(created);
        first.Enabled = true;
        var second = new CommonPluginInstanceConfig { PluginId = "test.plugin", InstanceId = "two", Enabled = true };
        await manager.ReconcileAsync([first, second], default);
        await manager.ReconcileAsync([first, second], default);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, host.Snapshot().Length);
        first.Enabled = false;
        await manager.ReconcileAsync([first, second], default);
        Assert.Equal("two", Assert.Single(host.Snapshot()).Instance.InstanceId);
        Assert.Equal(1, created[0].Disposals);
        await manager.StopAsync(Deadline);
        Assert.Empty(host.Snapshot());
        Assert.All(created, plugin => Assert.Equal(1, plugin.Disposals));
    }

    [Fact]
    public async Task AFailedPackageDoesNotBlockIndependentStartupOrAutomaticallyReload()
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "a.bad", "b.good");
        int loads = 0;
        CommonPluginManager manager = new(new(action => action()), installed, temporary.GetPath("state"), action => action(),
            (package, _) =>
            {
                loads++;
                if (package.Manifest.Id == "a.bad") { throw new InvalidOperationException("Fixture load failure"); }
                return Task.FromResult<IPlugin>(new FakePlugin(package.Manifest.Id));
            });
        CommonPluginInstanceConfig[] configuration =
            [new() { PluginId = "a.bad", Enabled = true }, new() { PluginId = "b.good", Enabled = true }];
        await manager.ReconcileAsync(configuration, default);
        await manager.ReconcileAsync(configuration, default);
        Assert.Equal(2, loads);
        Assert.NotNull(manager.Snapshot().Single(instance => instance.Identity.PluginId == "a.bad").Error);
        Assert.Equal(PluginHealth.Ready, manager.Snapshot().Single(instance => instance.Identity.PluginId == "b.good").Registration!.Health.Health);
        await manager.StopAsync(Deadline);
    }

    [Fact]
    public async Task UnconfirmedCleanupRetainsTheInstanceAndPreventsReplacement()
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "test.plugin");
        int loads = 0;
        FakePlugin plugin = new("test.plugin") { Released = false };
        CommonPluginManager manager = new(new(action => action()), installed, temporary.GetPath("state"), action => action(),
            (_, _) => { loads++; return Task.FromResult<IPlugin>(plugin); });
        var configuration = new CommonPluginInstanceConfig { PluginId = plugin.Id, Enabled = true };
        await manager.ReconcileAsync([configuration], default);
        configuration.Enabled = false;
        await manager.ReconcileAsync([configuration], default);
        Assert.NotNull(Assert.Single(manager.Snapshot()).Error);
        configuration.Enabled = true;
        await manager.ReconcileAsync([configuration], default);
        Assert.Equal(1, loads);
        Assert.Equal(1, plugin.Stops);
        Assert.Equal(1, plugin.Disposals);
        await Assert.ThrowsAsync<AggregateException>(() => manager.StopAsync(Deadline));
    }

    [Fact]
    public async Task ShutdownRetainsAnUnfinishedLoadAndDisposesItOnlyAfterCompletion()
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "test.plugin");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlugin plugin = new("test.plugin");
        CommonPluginManager manager = new(new(action => action()), installed, temporary.GetPath("state"), action => action(),
            async (_, _) => { entered.SetResult(); await release.Task; return plugin; });
        using CancellationTokenSource cancellation = new();
        var start = manager.ReconcileAsync([new() { PluginId = plugin.Id, Enabled = true }], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await start;
        await Assert.ThrowsAsync<AggregateException>(() => manager.StopAsync(DateTimeOffset.UtcNow.AddMilliseconds(100)));
        Assert.Equal(0, plugin.Disposals);
        release.SetResult();
        await manager.StopAsync(Deadline);
        Assert.Equal(0, plugin.Starts);
        Assert.Equal(1, plugin.Disposals);
        Assert.Empty(manager.Snapshot());
    }

    private static async Task<string> Catalog(TemporaryDirectory temporary, params string[] ids)
    {
        string installed = temporary.GetPath("plugins");
        foreach (string id in ids)
        {
            string root = Path.Combine(installed, id);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Fixture.dll"), "Metadata fixture");
            await File.WriteAllTextAsync(Path.Combine(root, "plugin.wsgm.json"), $$"""
                {"id":"{{id}}","name":"Fixture","version":"1.0","category":"example.status",
                 "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin"}
                """);
        }
        return installed;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewEnableIntentCannotStartAnObsoleteLoad(bool reenable)
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "test.plugin");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<FakePlugin> created = [];
        PluginHost host = new(action => action());
        CommonPluginManager manager = new(host, installed, temporary.GetPath("state"), action => action(), async (_, _) =>
        {
            FakePlugin plugin = new("test.plugin");
            created.Add(plugin);
            if (created.Count == 1) { entered.SetResult(); await release.Task; }
            return plugin;
        });
        var configuration = new CommonPluginInstanceConfig { PluginId = "test.plugin", Enabled = true };
        var initial = manager.ReconcileAsync([configuration], default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = manager.ReconcileAsync([], default);
        var latest = reenable ? manager.ReconcileAsync([configuration], default) : Task.CompletedTask;
        release.SetResult();
        await Task.WhenAll(initial, disable, latest);
        Assert.Equal(0, created[0].Starts);
        Assert.Equal(1, created[0].Disposals);
        Assert.Equal(reenable ? 1 : 0, host.Snapshot().Length);
        if (reenable) { Assert.Equal(1, created[1].Starts); }
        await manager.StopAsync(Deadline);
    }

    [Fact]
    public async Task RepeatedPowerMessagesAdvanceEachInstanceOnlyOncePerTransition()
    {
        using TemporaryDirectory temporary = new();
        string installed = await Catalog(temporary, "test.plugin");
        FakePlugin plugin = new("test.plugin");
        CommonPluginManager manager = new(new(action => action()), installed, temporary.GetPath("state"), action => action(),
            (_, _) => Task.FromResult<IPlugin>(plugin));
        await manager.ReconcileAsync([new() { PluginId = plugin.Id, Enabled = true }], default);
        await manager.PowerTransitionAsync(true, default);
        await manager.PowerTransitionAsync(true, default);
        await manager.PowerTransitionAsync(false, default);
        await manager.PowerTransitionAsync(false, default);
        Assert.Equal(1, plugin.Suspends);
        Assert.Equal(1, plugin.Resumes);
        Assert.Equal(2, Assert.Single(manager.Snapshot()).Registration!.Context.Generation);
        await manager.StopAsync(Deadline);
    }

    private sealed class FakePlugin(string id) : IPlugin
    {
        public string Id => id;
        internal int Starts { get; private set; }
        internal int Stops { get; private set; }
        internal int Disposals { get; private set; }
        internal int Suspends { get; private set; }
        internal int Resumes { get; private set; }
        internal bool Released { get; init; } = true;
        public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
        { Starts++; return ValueTask.FromResult(PluginHealth.Ready); }
        public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
        { Suspends++; return ValueTask.CompletedTask; }
        public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
        { Resumes++; return ValueTask.CompletedTask; }
        public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
        { Stops++; return ValueTask.FromResult(Released); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
