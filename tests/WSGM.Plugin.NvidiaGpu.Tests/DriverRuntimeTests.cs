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
    public async Task SupportDiscoveryReadsAllSettingsBeforeReturningTheirCurrentValues()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var first = new ProbeControl("first", events, false);
        var second = new ProbeControl("second", events, false);
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => new Session(first, second));
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);

        Assert.Equal(PluginHealth.Ready, await runtime.StartAsync(host, context, CancellationToken.None));
        Assert.Equal(["first.read", "second.read", "first.write:False", "second.write:False"], events.Take(4));
        Assert.Equal(2, host.Last!.Descriptors.Count);
        Assert.True(await runtime.StopAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task FailedSupportProbesAreOmittedBeforePublicationAndNeverRetriedOnRestart()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var accepted = new ProbeControl("accepted", events, false);
        var refused = new ProbeControl("refused", events, true);
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => new Session(accepted, refused));
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);

        await runtime.StartAsync(host, context, CancellationToken.None);
        Assert.Equal("accepted", Assert.Single(host.Last!.Descriptors).CapabilityId);
        Assert.True(await runtime.StopAsync(context, CancellationToken.None));
        host.CycleGeneration = 2;
        await runtime.StartAsync(host, context with { Generation = 2 }, CancellationToken.None);
        Assert.Equal("accepted", Assert.Single(host.Last!.Descriptors).CapabilityId);
        Assert.Equal(1, events.Count(item => item == "refused.write:False"));
        Assert.True(await runtime.StopAsync(context, CancellationToken.None));
    }

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
            Assert.False(await runtime.StopAsync(context, cancelled.Token));
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

    private sealed class ProbeControl(string name, List<string> events, bool refused)
        : DriverControl(DriverDescriptors.Toggle(name, "driver", name, "graphics", CapabilityProfileScope.GlobalOnly))
    {
        internal override CapabilityValue Read()
        {
            events.Add(name + ".read");
            return CapabilityValue.Boolean(false);
        }

        internal override void Write(CapabilityValue value, WriteAdmission admission)
        {
            admission.Check();
            events.Add(name + ".write:" + value.BooleanValue);
            if (refused)
            {
                throw new DriverFailure("Setter not supported", true);
            }
        }
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

        internal override void Write(CapabilityValue value, WriteAdmission admission)
        {
            if (value.BooleanValue == true)
            {
                entered.TrySetResult();
                release.Wait();
            }

            _value = value.BooleanValue == true;
        }
    }

    private sealed class Session(params DriverControl[] controls) : IDriverSession
    {
        internal bool Disposed { get; private set; }

        public void BeginPass()
        {
        }

        public DriverModel Discover()
        {
            return new DriverModel([DriverDescriptors.Section("graphics", "Graphics")], controls);
        }

        public ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, WriteAdmission admission,
            CancellationToken token)
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
