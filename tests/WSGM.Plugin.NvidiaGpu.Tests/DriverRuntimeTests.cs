using System.Collections.Concurrent;
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
    public async Task EachPassBeginsOnceAndSharedNativeStructuresAreProbedOnce()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var first = new ProbeControl("enabled", events, false) { SharedSupportKey = "structure" };
        var second = new ProbeControl("second", events, false) { SharedSupportKey = "structure" };
        var session = new Session(first, second) { Events = events };
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => session);
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            Assert.Equal(new[] { "begin", "discover", "enabled.read", "second.read", "enabled.write:False" },
                events.Take(5));
            Assert.Equal(2, host.Last!.Descriptors.Count);
            Assert.Equal(1, events.Count(item => item == "begin"));
            Assert.DoesNotContain(events, item => item.StartsWith("second.write:"));
            await runtime.ExecuteCommandAsync(Command(host), CancellationToken.None);
            Assert.Equal(2, events.Count(item => item == "begin"));
            Assert.Equal(2, events.Count(item => item.StartsWith("enabled.write:")));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ATransientSupportReadRecoversOnTheNextPassDespiteAnExpiredLifecycleContext()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var control = new ProbeControl("enabled", events, false) { FailedReads = 1 };
        var host = new Host();
        var session = new Session(control);
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => session);
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            Assert.Empty(host.Last!.Descriptors);
            Assert.DoesNotContain(events, item => item.Contains(".write:"));
            await runtime.SessionChangedAsync(context with { Deadline = Deadline.Expired }, CancellationToken.None);
            await host.StatePublished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Single(host.Last.Descriptors);
            Assert.Equal(1, events.Count(item => item == "enabled.write:False"));
            Assert.False(session.Disposed);
            Assert.DoesNotContain(host.Traces, trace => trace.Contains("Driver observation stopped"));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RefusedDescriptorsArePublishedAgainWithoutDisposingTheSession()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var host = new Host { RefuseDescriptors = 1 };
        var session = new Session(new ProbeControl("enabled", events, false));
        var opens = 0;
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) =>
        {
            opens++;
            return session;
        });
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            Assert.Null(host.Last);
            Assert.False(session.Disposed);
            await host.DescriptorsPublished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, host.DescriptorAttempts);
            Assert.Single(host.Last!.Descriptors);
            Assert.Equal(1, opens);
            Assert.False(session.Disposed);
            Assert.Equal(1, events.Count(item => item == "enabled.write:False"));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RefusedPostWriteStateDoesNotTurnAnAcceptedWriteIntoAFault()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var host = new Host();
        var session = new Session(new ProbeControl("enabled", events, false));
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => session);
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            host.RefuseState = true;
            var result = await runtime.ExecuteCommandAsync(Command(host), CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
            Assert.Null(result.Reason);
            Assert.False(session.Disposed);
            host.RefuseState = false;
            host.StatePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await host.StatePublished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(host.LastState);
            Assert.False(session.Disposed);
            Assert.Equal(2, events.Count(item => item.Contains(".write:")));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationDuringPreparationRejectsWithoutDispatchingTheSetter()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<string>();
        var control = new ProbeControl("enabled", events, false);
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => new Session(control));
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            control.BeforeWrite = cancellation.Cancel;
            var result = await runtime.ExecuteCommandAsync(Command(host), cancellation.Token);
            Assert.Equal(CommandOutcome.Rejected, result.Outcome);
            Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason!.Code);
            Assert.Equal(1, events.Count(item => item.Contains(".write:")));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledOrExpiredCommandDoesNotDispatch(bool cancelled)
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture",
            (_, _) => new Session(new ProbeControl("enabled", events, false)));
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);
        try
        {
            var command = Command(host) with { Deadline = cancelled ? Deadline.Never : Deadline.Expired };
            var result = await runtime.ExecuteCommandAsync(command, new CancellationToken(cancelled));
            Assert.Equal(CommandOutcome.Rejected, result.Outcome);
            Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason!.Code);
            Assert.Equal(1, events.Count(item => item.Contains(".write:")));
        }
        finally
        {
            await runtime.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public void DescriptorLabelsRetainAllSafeCharacters()
    {
        var label = new string('L', 100);
        var descriptor = DriverDescriptors.Toggle("enabled", "driver", label + "\u0001", "graphics",
            CapabilityProfileScope.GlobalOnly);
        Assert.Equal(label, descriptor.Display.CustomLabel);
    }

    [Fact]
    public void ActionAdmissionRequiresNullInsteadOfAValue()
    {
        var descriptor =
            DriverDescriptors.Toggle("cache-reset", "gpu", "Cache", "graphics", CapabilityProfileScope.GlobalOnly) with
            {
                SupportsAction = true,
                SupportsRead = false,
                SupportsWrite = false,
                ValueKind = CapabilityValueKind.None
            };
        var control = new ActionControl(descriptor);
        Assert.True(control.Accepts(null));
        Assert.False(control.Accepts(CapabilityValue.None()));
        Assert.False(control.Accepts(CapabilityValue.Boolean(true)));
    }

    [Fact]
    public async Task AcceptedWriteWithDifferentReadbackPublishesTheWrittenValueWithoutAFault()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
        var control = new ProbeControl("enabled", events, false);
        var host = new Host();
        await using var runtime = new DriverRuntime("wsgm.gpu.fixture", (_, _) => new Session(control));
        var context = new PluginContext(new PluginInstanceIdentity(runtime.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory.Root);
        await runtime.StartAsync(host, context, CancellationToken.None);

        var written = CapabilityValue.Boolean(true);
        var result = await runtime.ExecuteCommandAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = "enabled",
            InstanceId = "driver",
            ExpectedCycleGeneration = 1,
            ExpectedDescriptorGeneration = host.Last!.Generation,
            Deadline = Deadline.Never,
            RequestedValue = written
        }, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Null(result.ReadbackValue);
        Assert.Equal(written, host.LastState!.ObservedValue);
        Assert.Equal(HardwareStateQuality.Observed, host.LastState.Quality);
    }

    [Fact]
    public async Task SupportDiscoveryReadsAllSettingsBeforeReturningTheirCurrentValues()
    {
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<string>();
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
        var events = new ConcurrentQueue<string>();
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

    private static CapabilityCommand Command(Host host)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = "enabled",
            InstanceId = "driver",
            ExpectedCycleGeneration = host.CycleGeneration,
            ExpectedDescriptorGeneration = host.Last!.Generation,
            Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Boolean(true)
        };
    }

    private sealed class ProbeControl(string name, ConcurrentQueue<string> events, bool refused)
        : DriverControl(DriverDescriptors.Toggle(name, "driver", name, "graphics", CapabilityProfileScope.GlobalOnly))
    {
        internal int FailedReads { get; set; }
        internal Action? BeforeWrite { get; set; }
        internal string? SharedSupportKey { get; init; }
        internal override string SupportKey => SharedSupportKey ?? base.SupportKey;

        internal override CapabilityValue Read()
        {
            events.Enqueue(name + ".read");
            if (FailedReads > 0)
            {
                FailedReads--;
                throw new DriverFailure("Transient read failure.");
            }

            return CapabilityValue.Boolean(false);
        }

        internal override void Write(CapabilityValue value, WriteAdmission admission)
        {
            BeforeWrite?.Invoke();
            admission.Check();
            events.Enqueue(name + ".write:" + value.BooleanValue);
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
            admission.Check();
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
        internal ConcurrentQueue<string>? Events { get; init; }

        public void BeginPass()
        {
            Events?.Enqueue("begin");
        }

        public DriverModel Discover()
        {
            Events?.Enqueue("discover");
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
        internal CapabilityState? LastState { get; private set; }
        internal int RefuseDescriptors { get; init; }
        internal int DescriptorAttempts { get; private set; }
        internal bool RefuseState { get; set; }

        internal TaskCompletionSource DescriptorsPublished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StatePublished { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentQueue<string> Traces { get; } = new();
        public long CycleGeneration { get; set; } = 1;

        public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors,
            CancellationToken cancellationToken)
        {
            DescriptorAttempts++;
            if (DescriptorAttempts <= RefuseDescriptors)
            {
                throw new InvalidOperationException("Fixture refused descriptors.");
            }

            Last = descriptors;
            DescriptorsPublished.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken cancellationToken)
        {
            if (RefuseState)
            {
                throw new InvalidOperationException("Fixture refused state.");
            }

            LastState = state;
            StatePublished.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
            Traces.Enqueue(message);
        }

        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
            Traces.Enqueue(message);
        }

        public ICapabilityHost Capabilities => this;

        public void PublishHealth(PluginHealthPublication publication)
        {
        }
    }

    private sealed class ActionControl(CapabilityDescriptor descriptor) : DriverControl(descriptor)
    {
        internal override CapabilityValue Read()
        {
            return CapabilityValue.None();
        }

        internal override void Write(CapabilityValue value, WriteAdmission admission)
        {
            admission.Check();
        }
    }
}
