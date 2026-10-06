using System.Collections.Concurrent;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class IntelGpuPluginTests
{
    [Fact]
    public async Task DiscoverySkipsNonRowsAndPublishesTheReportedWindowedVrrControl()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver
        {
            Features = new[]
            {
                ThreeDFeatureCatalog.AppProfileDetails, ThreeDFeatureCatalog.GlobalOrPerApp,
                ThreeDFeatureCatalog.FrameGenerationControl, ThreeDFeatureCatalog.VrrWindowedBlt
            }.Select(id => new Ctl3dFeatureDetails { FeatureType = id, ValueType = (int)IgclValueType.Enum }).ToArray()
        };
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            var vrr = Assert.Single(host.Last.Descriptors,
                descriptor => descriptor.CapabilityId == "graphics.vrr-windowed");
            Assert.Equal(new[] { "auto", "on", "off" }, vrr.Choices.Select(choice => choice.Value));
            Assert.Equal(2, host.Last.Descriptors.Count);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnreadablePerApplicationRecordDoesNotDisableGlobalControls()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "application-profiles.v1.json");
        File.WriteAllText(path, "corrupt ownership record");
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        Assert.Equal(PluginHealth.Ready, await plugin.StartAsync(host, context, CancellationToken.None));
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var command = Command(host);
            var sync = new ApplicationProfileSync(1, 1,
            [
                new ApplicationCapabilityProfile("game", "Game", ["game.exe"],
                [
                    new ApplicationCapabilityValue(command.CapabilityId, command.InstanceId, command.RequestedValue!)
                ])
            ]);
            Assert.Single((await plugin.SyncApplicationProfilesAsync(sync, CancellationToken.None)).Failures);
            Assert.Equal(CommandOutcome.AppliedVerified,
                (await plugin.ExecuteCommandAsync(command, CancellationToken.None)).Outcome);
            Assert.Equal("corrupt ownership record", File.ReadAllText(path));
            Assert.Equal(1, driver.Opens);
            Assert.Equal(0, driver.Closes);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedNativeSupportProbeIsNotRepeatedOnResume()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver { WriteResult = 0x40000017 };
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            Assert.Empty(host.Last.Descriptors);
            Assert.Equal(1, driver.Writes);
            await plugin.SuspendAsync(context, CancellationToken.None);
            host.CycleGeneration = 2;
            await plugin.ResumeAsync(context with { Generation = 2 }, CancellationToken.None);
            Assert.Empty(host.Last.Descriptors);
            Assert.Equal(1, driver.Writes);
            Assert.Equal(2, driver.Opens);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedSupportReadRecoversWithoutCachingASetterFailure()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver
            { ReadResult = read => read == 1 ? IgclResult.InvalidArgument : IgclResult.Success };
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(host.Descriptors.First().Descriptors);
            var descriptor = Assert.Single(host.Last.Descriptors);
            Assert.Equal("graphics.retro-scaling", descriptor.CapabilityId);
            Assert.Equal("pci-8086-4688-00-02-0", descriptor.InstanceId);
            Assert.Equal("graphics", Assert.Single(host.Last.Sections).SectionId);
            Assert.Equal(1, driver.Writes);
            Assert.Equal(1, driver.Opens);
            Assert.Equal(0, driver.Closes);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(IgclResult.UnsupportedFeature)]
    [InlineData(IgclResult.NotImplemented)]
    [InlineData(IgclResult.PlatformNotSupported)]
    [InlineData(IgclResult.SetFbcNotSupported)]
    public async Task ExplicitUnsupportedReadPublishesNoControlAndDoesNotProbe(int result)
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver { ReadResult = _ => result };
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        Assert.Empty(host.Last.Descriptors);
        Assert.Equal(0, driver.Writes);
        Assert.Equal(0, driver.Closes);
        Assert.True(await plugin.StopAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task RefusedDescriptorPublicationRetriesWithoutClosingTheSession()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver();
        var host = new Host { RefuseDescriptors = 1 };
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, host.DescriptorAttempts);
            Assert.Single(host.Last.Descriptors);
            Assert.Equal(2, host.Last.Generation);
            Assert.Equal(1, driver.Opens);
            Assert.Equal(0, driver.Closes);
            Assert.Equal(1, driver.Writes);
            Assert.All(host.Health, health => Assert.Equal(PluginHealth.Ready, health.Health));
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CommandWriteAndReadbackLeaveTheCallerThread()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            driver.ReadThreads.Clear();
            driver.WriteThreads.Clear();
            var completion = new TaskCompletionSource<(int Caller, CapabilityCommandResult Result)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var caller = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                try
                {
                    var result = plugin.ExecuteCommandAsync(Command(host), CancellationToken.None).AsTask()
                        .GetAwaiter().GetResult();
                    completion.SetResult((Environment.CurrentManagedThreadId, result));
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            }) { IsBackground = true };
            caller.Start();
            var (thread, outcome) = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CommandOutcome.AppliedVerified, outcome.Outcome);
            Assert.NotEmpty(driver.WriteThreads);
            Assert.NotEmpty(driver.ReadThreads);
            Assert.All(driver.WriteThreads.Concat(driver.ReadThreads),
                nativeThread => Assert.NotEqual(thread, nativeThread));
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationAfterAcceptedWriteStillPublishesTheWrittenValue()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var driver = new FakeIgclDriver { IgnoreWrites = true };
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            driver.BeforeWrite = cancellation.Cancel;
            host.CheckPublicationToken = true;
            var result = await plugin.ExecuteCommandAsync(Command(host), cancellation.Token);
            Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
            Assert.Null(result.Reason);
            Assert.Equal(CapabilityValue.Choice("integer"), host.States.Last().ObservedValue);
            Assert.Equal(HardwareStateQuality.Observed, host.States.Last().Quality);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancelledCommandBeforeAdmissionIsRejectedWithoutASetter()
    {
        using var directory = new TemporaryDirectory();
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        try
        {
            await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var writes = driver.Writes;
            var result = await plugin.ExecuteCommandAsync(Command(host), new CancellationToken(true));
            Assert.Equal(CommandOutcome.Rejected, result.Outcome);
            Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason!.Code);
            Assert.Equal(writes, driver.Writes);
        }
        finally
        {
            await plugin.StopAsync(context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task TimedOutSuspendAndResumeDoNotOpenUnderABlockedCall()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.BeforeWrite = () =>
        {
            entered.TrySetResult();
            release.Wait();
        };
        var command = plugin.ExecuteCommandAsync(Command(host), CancellationToken.None).AsTask();
        Task? resume = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                plugin.SuspendAsync(context, new CancellationToken(true)).AsTask());
            host.CycleGeneration = 2;
            resume = plugin.ResumeAsync(context with { Generation = 2 }, CancellationToken.None).AsTask();
            Assert.False(resume.IsCompleted);
            Assert.Equal(1, driver.Opens);
            Assert.Equal(0, driver.Closes);
        }
        finally
        {
            release.Set();
            await command.WaitAsync(TimeSpan.FromSeconds(5));
            if (resume is not null)
            {
                await resume.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await plugin.StopAsync(context, CancellationToken.None);
        }

        Assert.Equal(2, driver.Opens);
        Assert.Equal(2, driver.Closes);
        Assert.Equal(2, driver.Writes);
        Assert.Equal(2, host.Last.CycleGeneration);
    }

    [Fact]
    public async Task StopReturnsWithinItsBudgetWhileCloseRetainsTheNativeLane()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.BeforeClose = () =>
        {
            entered.TrySetResult();
            release.Wait();
        };
        Task<PluginHealth>? restart = null;
        try
        {
            Assert.False(await plugin.StopAsync(context, new CancellationToken(true)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.CycleGeneration = 2;
            restart = plugin.StartAsync(host, context with { Generation = 2 }, CancellationToken.None).AsTask();
            Assert.False(restart.IsCompleted);
            Assert.Equal(1, driver.Opens);
            Assert.Equal(0, driver.Closes);
        }
        finally
        {
            release.Set();
            if (restart is not null)
            {
                await restart.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await plugin.StopAsync(context, CancellationToken.None);
        }

        Assert.Equal(2, driver.Opens);
        Assert.Equal(2, driver.Closes);
    }

    [Fact]
    public async Task BoundedStopKeepsTheDriverOpenUntilTheBlockedSetterReturns()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeIgclDriver();
        var host = new Host();
        await using var plugin = driver.CreatePlugin();
        var context = Context(plugin, directory.Root);
        await plugin.StartAsync(host, context, CancellationToken.None);
        await host.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.BeforeWrite = () =>
        {
            entered.TrySetResult();
            release.Wait();
        };
        var command = plugin.ExecuteCommandAsync(Command(host), CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await plugin.StopAsync(context, new CancellationToken(true)));
            Assert.Equal(0, driver.Closes);
            Assert.Equal(1, driver.Opens);
        }
        finally
        {
            release.Set();
            await command.WaitAsync(TimeSpan.FromSeconds(5));
            await plugin.StopAsync(context, CancellationToken.None);
        }

        Assert.Equal(1, driver.Closes);
    }

    private static PluginContext Context(IntelGpuPlugin plugin, string directory)
    {
        return new PluginContext(new PluginInstanceIdentity(plugin.Id, "default"), 1,
            PluginSessionMode.Desktop, Deadline.Never, directory);
    }

    private static CapabilityCommand Command(Host host)
    {
        var descriptor = Assert.Single(host.Last.Descriptors);
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            ExpectedCycleGeneration = host.CycleGeneration,
            ExpectedDescriptorGeneration = host.Last.Generation,
            Deadline = Deadline.Never,
            RequestedValue = CapabilityValue.Choice("integer")
        };
    }

    private sealed class Host : IPluginHost, ICapabilityHost
    {
        private int _descriptorAttempts;
        internal ConcurrentQueue<CapabilityDescriptorSet> Descriptors { get; } = new();
        internal ConcurrentQueue<CapabilityState> States { get; } = new();
        internal ConcurrentQueue<PluginHealthPublication> Health { get; } = new();
        internal TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CapabilityDescriptorSet Last => Descriptors.Last();
        internal int RefuseDescriptors { get; init; }
        internal int DescriptorAttempts => Volatile.Read(ref _descriptorAttempts);
        internal bool CheckPublicationToken { get; set; }
        public long CycleGeneration { get; set; } = 1;

        public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken token)
        {
            if (Interlocked.Increment(ref _descriptorAttempts) <= RefuseDescriptors)
            {
                throw new InvalidOperationException("Fixture refused descriptors.");
            }

            Descriptors.Enqueue(descriptors);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken token)
        {
            if (CheckPublicationToken)
            {
                token.ThrowIfCancellationRequested();
            }

            States.Enqueue(state);
            Observed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ICapabilityHost Capabilities => this;

        public void PublishHealth(PluginHealthPublication publication)
        {
            Health.Enqueue(publication);
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
        }

        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
        }
    }
}
