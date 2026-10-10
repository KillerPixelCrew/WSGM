using System.Collections.Concurrent;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class BuiltinGpuServiceTests
{
    private static AppConfig Enabled(bool enabled = true)
    {
        return new AppConfig { GpuDrivers = new GpuDriverConfig { Intel = enabled } };
    }

    [Theory]
    [InlineData(GpuHealth.Failed)]
    [InlineData(GpuHealth.Unavailable)]
    public async Task CompletedFailedStartupIsRetiredBeforeTheEnabledVendorStartsAgain(GpuHealth health)
    {
        using var store = new TemporaryConfigStore();
        var first = new FakeDriver();
        first.Finish.SetResult(health);
        var second = new FakeDriver();
        second.Finish.SetResult(GpuHealth.Ready);
        var owners = new Queue<FakeDriver>([first, second]);
        await using var service = Service(store, owners);

        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(0, first.Disposals);
        Assert.True(first.Closed);
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Starts);
        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(1, second.Starts);
    }

    [Fact]
    public async Task StartupExceptionDoesNotStrandAnEnabledVendor()
    {
        using var store = new TemporaryConfigStore();
        var first = new FakeDriver();
        first.Finish.SetException(new InvalidOperationException("temporary native failure"));
        var second = new FakeDriver();
        second.Finish.SetResult(GpuHealth.Ready);
        var reports = new ConcurrentQueue<string>();
        await using var service = Service(store, new Queue<FakeDriver>([first, second]), reports);

        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Starts);
        Assert.Contains(reports, report => report.Contains("temporary native failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimedOutStartupKeepsOneUncancelledOwnerAndAcceptsItsLateSuccess()
    {
        using var store = new TemporaryConfigStore();
        var owner = new FakeDriver();
        var owners = new Queue<FakeDriver>([owner]);
        await using var service = Service(store, owners);

        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        var startup = service.StartupCompletion;
        Assert.False(startup.IsCompleted);
        Assert.False(owner.StartToken.CanBeCanceled);
        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(1, owner.Starts);
        Assert.Empty(owners);

        owner.Finish.SetResult(GpuHealth.Ready);
        await startup;
        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(1, owner.Starts);
        Assert.Equal(0, owner.Disposals);
        Assert.False(owner.Closed);
    }

    [Fact]
    public async Task LateStartupFailureCanBeRetiredAndRetriedOnTheNextReconciliation()
    {
        using var store = new TemporaryConfigStore();
        var first = new FakeDriver();
        var second = new FakeDriver();
        second.Finish.SetResult(GpuHealth.Ready);
        await using var service = Service(store, new Queue<FakeDriver>([first, second]));

        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        var startup = service.StartupCompletion;
        first.Finish.SetResult(GpuHealth.Failed);
        await startup;
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Starts);
    }

    [Fact]
    public async Task CancellingReconciliationOnlyCancelsTheWaitAndKeepsTheNativeStartupTracked()
    {
        using var store = new TemporaryConfigStore();
        using var cancellation = new CancellationTokenSource();
        var owner = new FakeDriver();
        await using var service = Service(store, new Queue<FakeDriver>([owner]),
            startupWait: TimeSpan.FromSeconds(5));
        var reconcile = service.ReconcileAsync(Enabled(), cancellation.Token);
        await owner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startup = service.StartupCompletion;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile);

        Assert.False(owner.StartToken.CanBeCanceled);
        Assert.False(startup.IsCompleted);
        owner.Finish.SetResult(GpuHealth.Ready);
        await startup;
        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(1, owner.Starts);
    }

    [Fact]
    public async Task DisablingWhileStartupIsPendingJoinsItBeforeDisposalAndReplacement()
    {
        using var store = new TemporaryConfigStore();
        var first = new FakeDriver();
        var second = new FakeDriver();
        second.Finish.SetResult(GpuHealth.Ready);
        await using var service = Service(store, new Queue<FakeDriver>([first, second]));
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        var disabled = service.ReconcileAsync(Enabled(false), CancellationToken.None);
        await first.AdmissionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disabled.IsCompleted);
        Assert.Equal(0, first.Disposals);
        Assert.Equal(0, second.Starts);
        first.Finish.SetResult(GpuHealth.Ready);
        await disabled;
        Assert.Equal(1, first.Disposals);

        await service.ReconcileAsync(Enabled(), CancellationToken.None);
        Assert.Equal(1, second.Starts);
    }

    [Fact]
    public async Task ShutdownDeadlineLeavesTrackedRetirementWhichRepeatedStopJoins()
    {
        using var store = new TemporaryConfigStore();
        var owner = new FakeDriver();
        await using var service = Service(store, new Queue<FakeDriver>([owner]));
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        await service.StopAsync(Deadline.After(TimeSpan.FromMilliseconds(10)));
        Assert.True(owner.Closed);
        Assert.Equal(0, owner.Disposals);
        owner.Finish.SetResult(GpuHealth.Ready);
        await service.StopAsync(Deadline.After(TimeSpan.FromSeconds(5)));
        await service.ReconcileAsync(Enabled(), CancellationToken.None);

        Assert.Equal(1, owner.Starts);
        Assert.Equal(1, owner.Disposals);
    }

    private static BuiltinGpuService Service(TemporaryConfigStore store, Queue<FakeDriver> owners,
        ConcurrentQueue<string>? reports = null, TimeSpan? startupWait = null)
    {
        return new BuiltinGpuService(store.Store, () => [GpuVendor.Intel], (_, _) => owners.Dequeue(),
            startupWait ?? TimeSpan.FromMilliseconds(10), (message, _) => reports?.Enqueue(message));
    }

    private sealed class FakeDriver : IBuiltinGpuDriver
    {
        internal readonly TaskCompletionSource
            AdmissionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource<GpuHealth> Finish =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        internal int Disposals;
        internal CancellationToken StartToken;
        internal int Starts;

        public ValueTask<GpuHealth> StartAsync(CancellationToken token)
        {
            Starts++;
            StartToken = token;
            Started.TrySetResult();
            return new ValueTask<GpuHealth>(Finish.Task);
        }

        public ValueTask RefreshTopologyAsync(CancellationToken token)
        {
            return ValueTask.CompletedTask;
        }

        public void CloseAdmission()
        {
            Closed = true;
            AdmissionClosed.TrySetResult();
        }

        public void SetSuspended(bool suspended)
        {
        }

        public ValueTask DisposeAsync()
        {
            Assert.True(Finish.Task.IsCompleted);
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
