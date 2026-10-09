using System.Diagnostics;
using LibHandheld;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Interop;
using WSGM.Shell;
using WSGM.Tests.Builders;
using WSGM.Tests.Fakes;
using WSGM.Tests.Input;
using DeviceIdentitySnapshot = LibHandheld.Contracts.DeviceIdentitySnapshot;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace WSGM.Tests.Shell;

public sealed class DeviceCoordinatorTests
{
    [Fact]
    public async Task IntegrationOffSkipsDiscoveryRuntimePowerNotificationsAndControllerTargets()
    {
        await using Harness harness = new(false);
        await harness.Coordinator.InitializeAsync();
        await harness.Coordinator.SuspendAsync();
        await harness.Coordinator.ResumeAsync(true);
        await harness.Coordinator.ApplyConfigAsync(harness.Config);

        Assert.Equal(DeviceCycleState.Disabled, harness.Coordinator.State);
        Assert.Equal(0, harness.Discoveries);
        Assert.Equal(0, harness.Loads);
        Assert.Equal(0, harness.PowerRegistrations);
        Assert.Empty(harness.Calls);
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("create:"));
    }

    [Fact]
    public async Task UnsupportedIdentityLeavesTheOwnerPassiveWithoutLoadingRuntime()
    {
        await using Harness harness = new();
        harness.Definition = null;

        await harness.Coordinator.InitializeAsync();

        Assert.Equal(DeviceCycleState.Passive, harness.Coordinator.State);
        Assert.False(harness.Coordinator.HasDevice);
        Assert.Equal(0, harness.Loads);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task SuspendAndResumeRetainTheSameNativeOwnerAndOneLifecycle()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        var owner = harness.Runtime!.Device;

        await harness.Coordinator.SuspendAsync();
        Assert.Equal(DeviceCycleState.Suspended, harness.Coordinator.State);
        await harness.Coordinator.ResumeAsync(true);

        Assert.Equal(DeviceCycleState.Active, harness.Coordinator.State);
        Assert.Same(owner, harness.Runtime!.Device);
        Assert.Equal(1, harness.Loads);
        Assert.Equal(["detect", "start", "suspend", "resume"], harness.Calls);
    }

    [Fact]
    public async Task PassiveDetectionRetiresTheRuntimeAndLaterPowerEventsDoNotReloadIt()
    {
        await using Harness harness = new();
        harness.Identity = new DeviceIdentitySnapshot { SystemManufacturer = "passive-test" };
        await harness.Coordinator.InitializeAsync();
        await harness.Coordinator.SuspendAsync();
        await harness.Coordinator.ResumeAsync(false);

        Assert.Equal(DeviceCycleState.Passive, harness.Coordinator.State);
        Assert.Equal(1, harness.Loads);
        Assert.Equal(["detect", "dispose"], harness.Calls);
    }

    [Fact]
    public async Task CallerCanceledPartialStartStopsAndDisposesWithoutAutomaticRestart()
    {
        await using Harness harness = new();
        using CancellationTokenSource cancellation = new();
        harness.Hook = (call, token) =>
        {
            if (call == "start")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Coordinator.InitializeAsync(cancellation.Token));

        Assert.Equal(DeviceCycleState.Disabled, harness.Coordinator.State);
        Assert.Equal(["detect", "start", "stop", "dispose"], harness.Calls);
        Assert.Equal(1, harness.Loads);
        Assert.Equal(0, harness.RestartDelays);
        Assert.True(File.Exists(harness.StatePath("disposed.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartFaultExhaustsTwoRestartsThenExplicitRetryOrSleepStartsOneFreshCycle(bool afterSleep)
    {
        await using Harness harness = new();
        var fail = true;
        harness.Hook = (call, _) => call == "start" && fail
            ? Task.FromException(new IOException("fixture start failed"))
            : Task.CompletedTask;
        TaskCompletionSource faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Coordinator.StateChanged += state =>
        {
            if (state == DeviceCycleState.Faulted && harness.RestartDelays == 2
                                                  && harness.Calls.Count(call => call == "dispose") == 3)
            {
                faulted.TrySetResult();
            }
        };
        await harness.Coordinator.InitializeAsync();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, harness.Loads);
        Assert.Equal(2, harness.RestartDelays);
        Assert.Equal(3, harness.Calls.Count(call => call == "dispose"));
        fail = false;
        if (afterSleep)
        {
            await harness.Coordinator.ResumeAsync(true);
        }
        else
        {
            Assert.True(await harness.Coordinator.RetryAfterFaultAsync());
        }

        Assert.Equal(DeviceCycleState.Active, harness.Coordinator.State);
        Assert.Equal(4, harness.Loads);
    }

    [Fact]
    public async Task AFailedPluginStopStillDisposesAndAllowsTheNextExplicitEnableWithoutRetryingTheStop()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        harness.Hook = (call, _) => call == "stop"
            ? Task.FromException(new IOException("fixture stop failed"))
            : Task.CompletedTask;
        var disabled = new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig { Enabled = false }
        };

        var failure = await Assert.ThrowsAsync<IOException>(() => harness.Coordinator.ApplyConfigAsync(disabled));
        Assert.Equal("fixture stop failed", failure.Message);
        Assert.Equal(DeviceCycleState.Disabled, harness.Coordinator.State);
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));
        Assert.Equal(0, harness.RestartDelays);
        harness.Hook = null;
        await harness.Coordinator.ApplyConfigAsync(harness.Config);

        Assert.Equal(DeviceCycleState.Active, harness.Coordinator.State);
        Assert.Equal(2, harness.Loads);
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
    }

    [Fact]
    public async Task ThrowingStateSubscribersCannotSkipTheOwnersPluginStopAndDisposal()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        harness.Coordinator.StateChanged += state =>
        {
            if (state is DeviceCycleState.Deactivating or DeviceCycleState.Disabled)
            {
                throw new InvalidOperationException("fixture subscriber failed");
            }
        };
        var disabled = new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig { Enabled = false }
        };

        var failure =
            await Assert.ThrowsAsync<AggregateException>(() => harness.Coordinator.ApplyConfigAsync(disabled));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.All(failure.InnerExceptions, exception =>
            Assert.Equal("fixture subscriber failed", Assert.IsType<InvalidOperationException>(exception).Message));

        Assert.Equal(DeviceCycleState.Disabled, harness.Coordinator.State);
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));
        Assert.True(File.Exists(harness.StatePath("disposed.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDisposesRetiredOwnersOnceDespiteStopOrStateNotificationFailures(bool subscriberFails)
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        if (subscriberFails)
        {
            harness.Coordinator.StateChanged += state =>
            {
                if (state is DeviceCycleState.Deactivating or DeviceCycleState.Disabled)
                {
                    throw new InvalidOperationException("fixture subscriber failed");
                }
            };
        }
        else
        {
            harness.Hook = (call, _) => call == "stop"
                ? Task.FromException(new IOException("fixture stop failed"))
                : Task.CompletedTask;
        }

        await harness.Coordinator.ShutdownAsync(HandheldStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(5)));
        await harness.Coordinator.Completion;
        await harness.Coordinator.DisposeAsync();

        Assert.Equal(DeviceCycleState.Disabled, harness.Coordinator.State);
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));
        Assert.Equal(0, harness.RestartDelays);
        Assert.Equal(1, harness.PowerDisposals);
        Assert.Equal(1, harness.DiagnosticsDisposals);
        Assert.Equal(1, harness.OwnerDisposals);
        Assert.Throws<ObjectDisposedException>(() =>
            harness.Coordinator.PhysicalGlyphCatalog.ReplacePackageProfiles([]));
    }

    [Fact]
    public async Task ShutdownRetainsAnInFlightStopAndNativeOwnershipWithoutRetryingIt()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Hook = (call, _) =>
        {
            if (call != "stop")
            {
                return Task.CompletedTask;
            }

            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            var shutdown = harness.Coordinator.ShutdownAsync(HandheldStopReason.WsgmExiting,
                Deadline.After(TimeSpan.FromSeconds(1))).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Runtime!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Coordinator.DisposeAsync();

            Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
            Assert.DoesNotContain("dispose", harness.Calls);
            Assert.Equal(0, harness.OwnerDisposals);
            Assert.False(harness.Runtime.LateCleanup.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await harness.Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Runtime!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Runtime!.LateCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));
    }

    [Fact]
    public async Task CallerCanceledResumePropagatesWithoutRestartOrReplacingTheNativeOwner()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        var owner = harness.Runtime!.Device;
        await harness.Coordinator.SuspendAsync();
        using CancellationTokenSource cancellation = new();
        harness.Hook = (call, token) =>
        {
            if (call == "resume")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Coordinator.ResumeAsync(false, cancellation.Token));

        Assert.Equal(1, harness.Loads);
        Assert.Equal(0, harness.RestartDelays);
        Assert.Same(owner, harness.Runtime!.Device);
    }

    [Fact]
    public async Task FailedUnlockResumeReplacesTheRuntimeAndStopNotificationsCannotReactivateTheOwner()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        await harness.Coordinator.SuspendAsync();
        harness.Hook = (call, _) => call == "resume"
            ? Task.FromException(new IOException("fixture resume failed"))
            : Task.CompletedTask;
        await harness.Coordinator.ResumeAsync(false);

        Assert.Equal(DeviceCycleState.Active, harness.Coordinator.State);
        Assert.Equal(2, harness.Loads);
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));

        List<DeviceCycleState> stopping = [];
        harness.Coordinator.StateChanged += stopping.Add;
        await harness.Coordinator.ShutdownAsync(HandheldStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(5)));
        await harness.Coordinator.Completion;
        Assert.Equal([DeviceCycleState.Deactivating, DeviceCycleState.Disabled], stopping);
        Assert.True(harness.OwnerDisposed);
    }

    [Fact]
    public async Task AdmissionClosureRefusesQueuedSettingsAndConfigWithoutStoppingThePluginEarly()
    {
        await using Harness harness = new();
        await harness.Coordinator.InitializeAsync();
        await harness.Coordinator.SuspendAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Hook = (call, _) =>
        {
            if (call != "resume")
            {
                return Task.CompletedTask;
            }

            entered.TrySetResult();
            return release.Task;
        };
        var resume = harness.Coordinator.ResumeAsync(false);
        Task? setting = null;
        Task? reload = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            setting = harness.Coordinator.SetAutoTdpEnabledAsync(true);
            reload = harness.Coordinator.ApplyConfigAsync(new AppConfig
            {
                DeviceIntegration = new DeviceIntegrationConfig { Enabled = false }
            });
            Assert.False(setting.IsCompleted);
            Assert.False(reload.IsCompleted);

            harness.Coordinator.CloseAdmission();
            harness.Coordinator.CloseAdmission();
            Assert.DoesNotContain("stop", harness.Calls);
            Assert.DoesNotContain("dispose", harness.Calls);
        }
        finally
        {
            release.TrySetResult();
            await resume.WaitAsync(TimeSpan.FromSeconds(5));
            if (setting is not null)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => setting);
            }

            if (reload is not null)
            {
                try
                {
                    await reload.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Admission closes the queued reload's lifetime before it can apply configuration.
                }
            }
        }

        Assert.False(harness.Coordinator.AutoTdpEnabled);
        Assert.True(harness.Coordinator.IntegrationEnabled);
        await harness.Coordinator.ShutdownAsync(HandheldStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(5)));
        await harness.Coordinator.Completion;
        Assert.Equal(1, harness.Calls.Count(call => call == "stop"));
        Assert.Equal(1, harness.Calls.Count(call => call == "dispose"));
    }

    [Fact]
    public async Task AdmissionClosureKeepsOnlyTheAutoTdpRestoreRouteUntilOrderedShutdown()
    {
        await using Harness harness = new(false);
        FakeCapabilityPublisher publisher = new(CapabilityRole.PowerSustainedLimit);
        var descriptor = new CapabilityDescriptor
        {
            CapabilityId = "power.sustained-limit",
            Role = CapabilityRole.PowerSustainedLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Unit = CapabilityUnit.Watt,
            Minimum = 5,
            Maximum = 40,
            Step = 1
        };
        harness.Coordinator.Capabilities.Attach(publisher);
        publisher.Publish(CapabilityBuilders.Set(1, descriptor));
        publisher.PublishState(1, CapabilityBuilders.State(1, CapabilityValue.Integer(20)) with
        {
            CapabilityId = descriptor.CapabilityId
        });
        harness.Coordinator.CloseAdmission();

        foreach (var origin in new[]
                 {
                     CapabilityCommandOrigin.User, CapabilityCommandOrigin.AutomaticControl,
                     CapabilityCommandOrigin.ProfileRestore, CapabilityCommandOrigin.DesiredStateRestore
                 })
        {
            var refused = await harness.Coordinator.ExecuteCapabilityAsync(descriptor.CapabilityId, null,
                CapabilityValue.Integer(15), TimeSpan.FromSeconds(1), origin);
            Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        }

        var restored = await harness.Coordinator.ExecuteCapabilityAsync(descriptor.CapabilityId, null,
            CapabilityValue.Integer(20), TimeSpan.FromSeconds(1), CapabilityCommandOrigin.AutoTdp);

        Assert.Equal(CommandOutcome.AppliedVerified, restored.Outcome);
        Assert.Equal(20, Assert.Single(publisher.Commands).RequestedValue!.IntegerValue);
        await harness.Coordinator.ShutdownAsync(HandheldStopReason.WsgmExiting,
            Deadline.After(TimeSpan.FromSeconds(5)));
        await harness.Coordinator.Completion;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Coordinator.ExecuteCapabilityAsync(
            descriptor.CapabilityId, null, CapabilityValue.Integer(20), TimeSpan.FromSeconds(1),
            CapabilityCommandOrigin.AutoTdp));
        Assert.Single(publisher.Commands);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly DeterministicFakeControllerBackend Backend = new();
        internal readonly List<string> Calls = [];
        private readonly TemporaryConfigStore _temporary = new();
        internal HandheldDefinition? Definition;
        internal int DiagnosticsDisposals;
        internal int Discoveries;
        internal Func<string, CancellationToken, Task>? Hook;
        internal DeviceIdentitySnapshot Identity = new();
        internal int Loads;
        internal int OwnerDisposals;
        internal bool OwnerDisposed;
        internal int PowerDisposals;
        internal int PowerRegistrations;
        internal int RestartDelays;
        internal HandheldDeviceRuntime? Runtime;

        internal Harness(bool enabled = true)
        {
            Definition = new HandheldDefinition(NativeRuntimeEngine.DefinitionId,
                    NativeRuntimeEngine.FamilyId, "Runtime fixture", "fixture")
                { DeclaredRoles = [LibHandheld.Contracts.CapabilityRole.GenericToggle] };
            Config = new AppConfig
            {
                DeviceIntegration = new DeviceIntegrationConfig
                {
                    PreferencesSchemaVersion = DeviceIntegrationConfig.CurrentPreferencesSchemaVersion,
                    Enabled = enabled,
                    ControllerManagementEnabled = false
                }
            };
            ProfileService profiles = new(Config.Profiles, (_, _) => Task.FromResult(Config.Profiles));
            Coordinator = new DeviceCoordinator(Config, _temporary.Store, 0, new Owner(this),
                action => action(), profiles, () => 0, () => RtssOsdMetrics.Empty, _ => { },
                new WindowsPowerModes(new PowerSchemes(new UnusedPowerSchemeApi()), new UnusedPowerModeApi()),
                sink => new ControllerManager(Backend, sink,
                    new HidHideOwnership(new FakeHidHideControl(), new InMemoryHidHideOwnershipStore()),
                    @"C:\WSGM.Tests\WSGM.exe", new ControllerProcessPriority(
                        () => ProcessPriorityClass.Normal, _ => { }, _ => { }, _ => { })),
                () => Identity,
                (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Discoveries++;
                    return Task.FromResult<HandheldDefinition?>(Definition);
                },
                (definition, identity, token, root) =>
                {
                    Loads++;
                    token.ThrowIfCancellationRequested();
                    var directory = Path.Combine(root, definition.FamilyId);
                    var engine = new NativeRuntimeEngine(Calls,
                        (call, ct) => Hook?.Invoke(call, ct) ?? Task.CompletedTask);
                    var device = new HandheldDevice(definition, identity, directory, null, engine);
                    Runtime = new HandheldDeviceRuntime(device, directory);
                    return Task.FromResult(Runtime);
                },
                _ =>
                {
                    PowerRegistrations++;
                    return new PowerNotification(this);
                },
                () => true,
                (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    RestartDelays++;
                    return Task.CompletedTask;
                },
                (_, _) => new Diagnostics(this));
        }

        internal AppConfig Config { get; }
        internal DeviceCoordinator Coordinator { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Coordinator.DisposeAsync();
                await Coordinator.Completion;
            }
            finally
            {
                _temporary.Dispose();
            }
        }

        internal string StatePath(string name)
        {
            return Path.Combine(_temporary.Context.Root, "DeviceState",
                NativeRuntimeEngine.FamilyId, name);
        }

        private sealed class Diagnostics(Harness harness) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                harness.DiagnosticsDisposals++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class PowerNotification(Harness harness) : IDisposable
        {
            public void Dispose()
            {
                harness.PowerDisposals++;
            }
        }

        private sealed class Owner(Harness harness) : IDisposable
        {
            public void Dispose()
            {
                harness.OwnerDisposed = true;
                harness.OwnerDisposals++;
            }
        }

        private sealed class UnusedPowerModeApi : IPowerModeApi
        {
            public Guid Read()
            {
                throw new InvalidOperationException("Unexpected Windows mode read.");
            }

            public void Set(Guid mode)
            {
                throw new InvalidOperationException("Unexpected Windows mode write.");
            }
        }
    }
}
