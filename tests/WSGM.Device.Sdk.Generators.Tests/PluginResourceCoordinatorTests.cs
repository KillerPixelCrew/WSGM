using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Generators.Tests;

public sealed class PluginResourceCoordinatorTests
{
    private static readonly PluginResourceOperationContext Context = new(
        HostGeneration: 2,
        DeviceGeneration: 7,
        Deadline: new DateTimeOffset(2026, 8, 28, 12, 0, 10, TimeSpan.Zero));

    [Fact]
    public async Task ActivateAsync_KeepsHealthyResourceWhenAnotherResourceFails()
    {
        var operations = new List<string>();
        var healthy = new StubResource("fan", operations);
        var failing = new StubResource("controller", operations)
        {
            Acquire = (_, _) => throw new InvalidOperationException("exclusive access failed"),
        };
        var host = new TestPluginHostAdapter(2, 7);
        var coordinator = new PluginResourceCoordinator(host, [healthy, failing]);

        await coordinator.ActivateAsync(Context);

        Assert.Equal(ResourceState.Owned, coordinator.States["fan"]);
        Assert.Equal(ResourceState.Faulted, coordinator.States["controller"]);
        Assert.Equal(
            [ResourceState.Acquiring, ResourceState.Owned,
                ResourceState.Acquiring, ResourceState.Faulted],
            host.ResourceStates.Select(state => state.State));
        Assert.Equal(CapabilityReasonCode.TransportFaulted, host.ResourceStates[^1].Reason?.Code);
    }

    [Fact]
    public async Task ReleaseAsync_RestoresResourcesInReverseDeclarationOrder()
    {
        var operations = new List<string>();
        var first = new StubResource("first", operations);
        var second = new StubResource("second", operations);
        var coordinator = new PluginResourceCoordinator(
            new TestPluginHostAdapter(2, 7),
            [first, second]);
        await coordinator.ActivateAsync(Context);
        operations.Clear();

        await coordinator.ReleaseAsync(Context);

        Assert.Equal(["release:second", "release:first"], operations);
        Assert.Equal(ResourceState.Idle, coordinator.States["first"]);
        Assert.Equal(ResourceState.Idle, coordinator.States["second"]);
    }

    [Fact]
    public async Task ActivateAsync_CancellationUnwindsAttemptedResourcesInReverseOrder()
    {
        var operations = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var first = new StubResource("first", operations);
        var second = new StubResource("second", operations)
        {
            Acquire = (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<PluginResourceOperationResult>(
                    new OperationCanceledException(cancellation.Token));
            },
        };
        var coordinator = new PluginResourceCoordinator(
            new TestPluginHostAdapter(2, 7),
            [first, second]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.ActivateAsync(Context, cancellation.Token));

        Assert.Equal(
            ["acquire:first", "acquire:second", "release:second", "release:first"],
            operations);
        Assert.Equal(ResourceState.Idle, coordinator.States["first"]);
        Assert.Equal(ResourceState.Idle, coordinator.States["second"]);
    }

    [Fact]
    public async Task ReleaseAsync_CancellationMarksUnverifiedAndContinuesCleanup()
    {
        var operations = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var first = new StubResource("first", operations);
        var second = new StubResource("second", operations)
        {
            Release = (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<PluginResourceOperationResult>(
                    new OperationCanceledException(cancellation.Token));
            },
        };
        var coordinator = new PluginResourceCoordinator(
            new TestPluginHostAdapter(2, 7),
            [first, second]);
        await coordinator.ActivateAsync(Context);
        operations.Clear();

        await coordinator.ReleaseAsync(Context, cancellation.Token);

        Assert.Equal(["release:second", "release:first"], operations);
        Assert.Equal(ResourceState.ReleasedUnverified, coordinator.States["second"]);
        Assert.Equal(ResourceState.Idle, coordinator.States["first"]);
    }

    [Fact]
    public void Constructor_RejectsDuplicateResourceIdentifiers()
    {
        var operations = new List<string>();

        Assert.Throws<ArgumentException>(() => new PluginResourceCoordinator(
            new TestPluginHostAdapter(2, 7),
            [new StubResource("duplicate", operations), new StubResource("duplicate", operations)]));
    }

    private sealed class StubResource(string resourceId, List<string> operations) : IPluginResource
    {
        public string ResourceId { get; } = resourceId;

        public Func<PluginResourceOperationContext, CancellationToken,
            ValueTask<PluginResourceOperationResult>>? Acquire
        { get; init; }

        public Func<PluginResourceOperationContext, CancellationToken,
            ValueTask<PluginResourceOperationResult>>? Release
        { get; init; }

        public ValueTask<PluginResourceOperationResult> AcquireAsync(
            PluginResourceOperationContext context,
            CancellationToken cancellationToken)
        {
            operations.Add($"acquire:{ResourceId}");
            return Acquire?.Invoke(context, cancellationToken)
                ?? ValueTask.FromResult(new PluginResourceOperationResult(ResourceState.Owned));
        }

        public ValueTask<PluginResourceOperationResult> SuspendAsync(
            PluginResourceOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginResourceOperationResult(ResourceState.Owned));

        public ValueTask<PluginResourceOperationResult> ResumeAsync(
            PluginResourceOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginResourceOperationResult(ResourceState.Owned));

        public ValueTask<PluginResourceOperationResult> ReleaseAsync(
            PluginResourceOperationContext context,
            CancellationToken cancellationToken)
        {
            operations.Add($"release:{ResourceId}");
            return Release?.Invoke(context, cancellationToken)
                ?? ValueTask.FromResult(new PluginResourceOperationResult(ResourceState.Idle));
        }
    }
}
