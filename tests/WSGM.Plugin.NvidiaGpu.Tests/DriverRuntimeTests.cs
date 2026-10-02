using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.NvidiaGpu.Tests;

public sealed class DriverRuntimeTests
{
    [Fact]
    public async Task CancelledStopKeepsBlockedCallOwnedAndPreventsPrematureReopen()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new BlockingControl(entered, release);
        var sessions = new List<Session>();
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) =>
        {
            var session = new Session(control);
            sessions.Add(session);
            return session;
        });
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        Assert.Equal(PluginHealth.Ready, await runtime.StartAsync(host, context, CancellationToken.None));
        var write = runtime.ExecuteCommandAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "enabled", InstanceId = "driver",
            ExpectedCycleGeneration = 1, ExpectedDescriptorGeneration = host.Last!.Generation,
            Deadline = Deadline.Never, RequestedValue = CapabilityValue.Boolean(true)
        }, CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.StopAsync(context, cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.StartAsync(host, context, cancelled.Token).AsTask());
            Assert.Single(sessions);
            Assert.False(sessions[0].Disposed);
        }
        finally
        {
            release.Set();
        }

        await write;
        Assert.True(await runtime.StopAsync(context, CancellationToken.None));
        Assert.True(sessions[0].Disposed);
        host.CycleGeneration = 2;
        Assert.Equal(PluginHealth.Ready,
            await runtime.StartAsync(host, context with { Generation = 2 }, CancellationToken.None));
        Assert.Equal(2, sessions.Count);
    }

    private sealed class BlockingControl(TaskCompletionSource entered, ManualResetEventSlim release)
        : DriverControl(DriverDescriptors.Toggle("enabled", "driver", "Enabled", "graphics",
            CapabilityProfileScope.GlobalOnly))
    {
        private bool _value;

        internal override CapabilityValue Read()
        {
            return CapabilityValue.Boolean(_value);
        }

        internal override void Write(CapabilityValue value)
        {
            entered.TrySetResult();
            release.Wait();
            _value = value.BooleanValue == true;
        }
    }

    private sealed class Session(DriverControl control) : IDriverSession
    {
        internal bool Disposed { get; private set; }

        public DriverModel Discover()
        {
            return new DriverModel([DriverDescriptors.Section("graphics", "Graphics")], [control]);
        }

        public ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, CancellationToken token)
        {
            return new ApplicationProfileSyncResult(0, 0, []);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class Host : IPluginHost, ICapabilityHost
    {
        internal CapabilityDescriptorSet? Last { get; private set; }
        public long CycleGeneration { get; set; } = 1;

        public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors,
            CancellationToken cancellationToken)
        {
            Last = descriptors;
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
        }

        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
        }

        public ICapabilityHost Capabilities => this;

        public void PublishHealth(PluginHealthPublication publication)
        {
        }
    }
}
