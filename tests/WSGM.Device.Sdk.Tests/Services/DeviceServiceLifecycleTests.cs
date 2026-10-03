using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Services;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Tests.Services;

/// <summary>The service walks both first-party packages run their start, suspend, stop and rollback on.</summary>
[Collection("plugin-trace")]
public sealed class DeviceServiceLifecycleTests
{
    private static readonly DeviceCycleContext<string> Context = new(7, Deadline.After(TimeSpan.FromSeconds(10)), "id");
    private readonly List<string> _calls = [];

    [Fact]
    public async Task RollbackUsesItsActiveDeadlineAndStillAttemptsEveryService()
    {
        RecordingService[] services =
        [
            new("events", true, _calls),
            new("controller", true, _calls) { WaitForCancellation = true }
        ];
        var context = new DeviceCycleContext<string>(7, Deadline.After(TimeSpan.FromMilliseconds(100)), "id");
        TestPluginHostAdapter host = new(context.CycleGeneration);
        CapabilityDescriptorSet published = new() { Generation = 3, CycleGeneration = context.CycleGeneration };

        await DeviceServiceLifecycle.RollBackStartAsync(services, context, host, published).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["release controller", "release events"], _calls);
        Assert.All(services, service => Assert.Equal(DeviceServiceState.ReleasedUnverified, service.State));
        Assert.Empty(host.PhysicalDeviceSets);
        Assert.Empty(host.OemControlSets);
        Assert.Empty(host.DescriptorSets);
    }

    [Fact]
    public async Task ServicesAcquireInStartOrderAndReleaseInReverse()
    {
        var services = Services();

        await DeviceServiceLifecycle.AcquireAllAsync(services, Context, CancellationToken.None);
        await DeviceServiceLifecycle.ReleaseAllAsync(services, Context, CancellationToken.None);

        Assert.Equal(
            [
                "acquire events", "acquire power", "acquire controller", "release controller", "release power",
                "release events"
            ],
            _calls);
        Assert.All(services, service => Assert.Equal(DeviceServiceState.Idle, service.State));
    }

    [Fact]
    public async Task SuspendStopsOnlyTheSuspendableServicesInReleaseOrder()
    {
        var services = Services();
        await DeviceServiceLifecycle.AcquireAllAsync(services, Context, CancellationToken.None);
        _calls.Clear();

        await DeviceServiceLifecycle.SuspendAllAsync(services, Context, CancellationToken.None);

        Assert.Equal(["release controller", "release events"], _calls);
        Assert.Equal(DeviceServiceState.Owned, services[1].State);
    }

    [Fact]
    public async Task AFailedStartReleasesEveryServiceAndRetractsItsPublications()
    {
        var services = Services();
        await DeviceServiceLifecycle.AcquireAllAsync(services, Context, CancellationToken.None);
        TestPluginHostAdapter host = new(Context.CycleGeneration);
        CapabilityDescriptorSet published = new() { Generation = 3, CycleGeneration = Context.CycleGeneration };

        await DeviceServiceLifecycle.RollBackStartAsync(services, Context, host, published);

        Assert.All(services, service => Assert.Equal(DeviceServiceState.Idle, service.State));
        Assert.Empty(Assert.Single(host.PhysicalDeviceSets));
        Assert.Empty(Assert.Single(host.OemControlSets));
        var retracted = Assert.Single(host.DescriptorSets);
        Assert.Equal(4, retracted.Generation);
        Assert.Equal(Context.CycleGeneration, retracted.CycleGeneration);
        Assert.Empty(retracted.Descriptors);
    }

    [Fact]
    public async Task AServiceThatThrowsFaultsAloneAndTheWalkContinues()
    {
        RecordingService[] services =
        [
            new("events", true, _calls) { Failure = new IOException("The source is gone.") },
            new("power", false, _calls)
        ];

        await DeviceServiceLifecycle.AcquireAllAsync(services, Context, CancellationToken.None);

        Assert.Equal(DeviceServiceState.Faulted, services[0].State);
        Assert.Contains("The source is gone.", services[0].Reason?.Detail);
        Assert.Equal(DeviceServiceState.Owned, services[1].State);
    }

    [Fact]
    public async Task AReleaseCancelledByItsDeadlineIsUnverified()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        RecordingService service = new("controller", true, _calls)
        {
            Failure = new OperationCanceledException(cancelled.Token)
        };

        await DeviceServiceLifecycle.ReleaseAllAsync([service], Context, cancelled.Token);

        Assert.Equal(DeviceServiceState.ReleasedUnverified, service.State);
        Assert.Equal(CapabilityReasonCode.Quiescing, service.Reason?.Code);
    }

    private RecordingService[] Services()
    {
        return
        [
            new RecordingService("events", true, _calls),
            new RecordingService("power", false, _calls),
            new RecordingService("controller", true, _calls)
        ];
    }

    private sealed class RecordingService(string serviceId, bool suspendable, List<string> calls)
        : DeviceService<string>(serviceId)
    {
        /// <summary>Thrown by every operation instead of completing it.</summary>
        public Exception? Failure { get; init; }

        public bool WaitForCancellation { get; init; }

        public override bool Suspendable => suspendable;

        public override ValueTask<DeviceServiceResult> AcquireAsync(
            DeviceCycleContext<string> context,
            CancellationToken cancellationToken)
        {
            calls.Add("acquire " + ServiceId);
            return Failure is null
                ? ValueTask.FromResult(Set(DeviceServiceState.Owned))
                : ValueTask.FromException<DeviceServiceResult>(Failure);
        }

        public override async ValueTask<DeviceServiceResult> ReleaseAsync(
            DeviceCycleContext<string> context,
            CancellationToken cancellationToken)
        {
            calls.Add("release " + ServiceId);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Set(DeviceServiceState.Idle);
        }
    }
}
