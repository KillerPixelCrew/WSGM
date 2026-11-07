using LibGPUDriverInteract;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class GpuDriverUpdateServiceTests
{
    private static readonly GpuInstalledDriver Adapter = new("one", "VEN_8086&DEV_7D55", GpuVendor.Intel,
        "Intel GPU", "1.0.0.0", new DateTime(2026, 1, 1));

    [Fact]
    public async Task FailedRescanDropsTheFormerInstallOffer()
    {
        using var config = new TemporaryConfigStore();
        var reads = 0;
        await using var service = Service(config, () => ++reads == 1 ? [Adapter] : throw new IOException("lost adapter"));
        Assert.True(service.Check());
        await service.Completion;
        Assert.True(Assert.Single(service.Snapshot()).CanInstall);
        Assert.True(service.Check());
        await service.Completion;
        var failed = Assert.Single(service.Snapshot());
        Assert.Equal(GpuDriverUpdateStage.Failed, failed.Stage);
        Assert.Null(failed.Package);
        Assert.False(failed.CanInstall);
    }

    [Fact]
    public async Task AConfirmationCannotInstallAReplacementPackage()
    {
        using var config = new TemporaryConfigStore();
        var version = "2.0.0.0";
        await using var service = Service(config, () => [Adapter],
            (adapter, _) => Task.FromResult(Package(adapter, version)));
        Assert.True(service.Check());
        await service.Completion;
        var confirmed = Assert.Single(service.Snapshot()).Package!.Identity;
        version = "3.0.0.0";
        Assert.True(service.Check());
        await service.Completion;
        Assert.NotEqual(confirmed, Assert.Single(service.Snapshot()).Package!.Identity);
        Assert.False(service.Install(Adapter.InstanceId, confirmed));
        Assert.True(service.Completion.IsCompleted);
    }

    [Fact]
    public async Task DelayedDiscoveryCannotOverwriteAnExplicitCheck()
    {
        using var config = new TemporaryConfigStore();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var service = Service(config, () => { Interlocked.Increment(ref reads); return [Adapter]; },
            wait: token => settled.Task.WaitAsync(token));
        Assert.True(service.Check());
        await service.Completion;
        settled.SetResult();
        await service.StartupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await service.Completion;
        Assert.Equal(1, reads);
        Assert.Equal(GpuDriverUpdateStage.Available, Assert.Single(service.Snapshot()).Stage);
        Assert.Null(service.DiscoveryDetail);
    }

    [Fact]
    public async Task RecentCheckIsKeptAcrossLaunchesButAnOverdueCheckRunsAfterSettling()
    {
        using var config = new TemporaryConfigStore();
        config.Store.Update(settings =>
        {
            settings.GpuDriverUpdatesAutoCheck = true;
            settings.GpuDriverUpdatesLastCheckedAt = DateTimeOffset.UtcNow;
            return true;
        });
        var calls = 0;
        Task<GpuDriverPackage> Check(GpuInstalledDriver adapter, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Package(adapter));
        }

        for (var launch = 0; launch < 2; launch++)
        {
            await using var service = Service(config, () => [Adapter], Check, _ => Task.CompletedTask);
            await service.StartupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await service.Completion;
            Assert.Equal(0, calls);
        }

        config.Store.Update(settings =>
        {
            settings.GpuDriverUpdatesLastCheckedAt = DateTimeOffset.UtcNow.AddHours(-25);
            return true;
        });
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var overdue = Service(config, () => [Adapter], Check, token => settled.Task.WaitAsync(token));
        Assert.Equal(0, calls);
        settled.SetResult();
        await overdue.StartupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await overdue.Completion;
        Assert.Equal(1, calls);
        Assert.True(config.Store.Read().Config!.GpuDriverUpdatesLastCheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AdapterChecksRunConcurrentlyAndNoticesAreConsumedOnce()
    {
        using var config = new TemporaryConfigStore();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = Service(config, () => [Adapter, Adapter with { InstanceId = "two" }],
            async (adapter, token) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    bothStarted.SetResult();
                }

                await release.Task.WaitAsync(token);
                return Package(adapter);
            });
        Assert.True(service.Check());
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await service.Completion;
        Assert.All(service.Snapshot(), state => Assert.Equal(GpuDriverUpdateStage.Available, state.Stage));
        service.AnnounceFallback(service.Snapshot()[0]);
        Assert.Contains("Device > GPU", service.TakeAnnouncement());
        Assert.Null(service.TakeAnnouncement());
    }

    [Fact]
    public async Task NonWhqlSettingDefaultsOffRoundTripsAndImmediatelyChecksTheNewChannel()
    {
        using var config = new TemporaryConfigStore();
        var channels = new List<bool>();
        await using (var service = new GpuDriverUpdateService(config.Store, () => false, _ => null, action => action(),
            readInstalled: () => [Adapter], checkPackage: (adapter, beta, _) =>
            {
                channels.Add(beta);
                return Task.FromResult(Package(adapter));
            }, waitForSession: token => Task.Delay(Timeout.Infinite, token)))
        {
            Assert.False(service.IncludeNonWhql);
            service.SetIncludeNonWhql(true);
            await service.Completion;
            Assert.True(service.IncludeNonWhql);
            Assert.Equal(new[] { true }, channels);
            var reloaded = new WSGM.Core.ConfigStore(config.Context);
            Assert.True(reloaded.Read().Config!.GpuDriverUpdatesIncludeNonWhql);
            service.SetIncludeNonWhql(false);
            await service.Completion;
            Assert.False(service.IncludeNonWhql);
            Assert.Equal(new[] { true, false }, channels);
        }
        await using var restarted = Service(config, () => [Adapter]);
        Assert.False(restarted.IncludeNonWhql);
    }

    [Fact]
    public async Task ChannelChangeDuringAnActiveCheckQueuesAFreshCheckAndDropsTheOldResult()
    {
        using var config = new TemporaryConfigStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channels = new List<bool>();
        await using var service = new GpuDriverUpdateService(config.Store, () => false, _ => null, action => action(),
            readInstalled: () => [Adapter], checkPackage: async (adapter, beta, token) =>
            {
                channels.Add(beta);
                if (!beta)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(token);
                }
                return Package(adapter, beta ? "3.0.0.0" : "2.0.0.0");
            }, waitForSession: token => Task.Delay(Timeout.Infinite, token));
        Assert.True(service.Check());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SetIncludeNonWhql(true);
        release.SetResult();
        await service.Completion;
        Assert.Equal(new[] { false, true }, channels);
        Assert.Equal("3.0.0.0", Assert.Single(service.Snapshot()).Package!.Version);
    }

    private static GpuDriverUpdateService Service(TemporaryConfigStore config,
        Func<IReadOnlyList<GpuInstalledDriver>> read,
        Func<GpuInstalledDriver, CancellationToken, Task<GpuDriverPackage>>? check = null,
        Func<CancellationToken, Task>? wait = null)
    {
        return new GpuDriverUpdateService(config.Store, () => false, _ => null, action => action(),
            readInstalled: read, checkPackage: (adapter, _, token) => check is null ? Task.FromResult(Package(adapter)) : check(adapter, token),
            waitForSession: wait ?? (token => Task.Delay(Timeout.Infinite, token)));
    }

    private static GpuDriverPackage Package(GpuInstalledDriver adapter, string version = "2.0.0.0") => new(adapter,
        version, new DateTime(2026, 11, 1), new Uri("https://downloadmirror.intel.com/driver.exe"),
        new Uri("https://www.intel.com/notes"), "Notes", 1024, null);
}
