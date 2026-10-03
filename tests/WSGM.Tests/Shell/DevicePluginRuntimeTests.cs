using System.Globalization;
using WSGM.Core;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Builders;
using PluginManifest = WSGM.Device.Sdk.Packaging.PluginManifest;

namespace WSGM.Tests.Shell;

public sealed class DevicePluginRuntimeTests
{
    private const long InitialGeneration = 41;

    [Fact]
    public async Task ControllerReleaseReturnsAtTheDeadlineAndKeepsTheLifecycleLaneUntilThePluginReturns()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(RuntimeFixturePlugin.ControllerReleaseKey, held.Task);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ReleaseControllerAsync(
                HandoffScope.ControllerOnly, Deadline.After(TimeSpan.FromMilliseconds(50)), CancellationToken.None));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.SetControllerManagementAsync(
                true, Deadline.After(TimeSpan.FromMilliseconds(50)), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(runtime.StateDirectory, "disposed.txt")));
        }
        finally
        {
            held.TrySetResult();
            AppContext.SetData(RuntimeFixturePlugin.ControllerReleaseKey, null);
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmergencyStopThatIgnoresCancellationReturnsAtItsDeadlineWithoutDisposingThePlugin()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(RuntimeFixturePlugin.StopKey, held.Task);
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => runtime.DisposeAsync(
                Deadline.After(TimeSpan.FromMilliseconds(50))).AsTask());
            Assert.False(File.Exists(Path.Combine(runtime.StateDirectory, "disposed.txt")));
            Assert.Throws<IOException>(() =>
            {
                using FileStream exclusive = new(temporary.GetPath("runtime.wsgmpkg"), FileMode.Open,
                    FileAccess.Read, FileShare.None);
            });
        }
        finally
        {
            held.TrySetResult();
            AppContext.SetData(RuntimeFixturePlugin.StopKey, null);
            await runtime.LateCleanup.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.True(File.Exists(Path.Combine(runtime.StateDirectory, "disposed.txt")));
        using FileStream released = new(temporary.GetPath("runtime.wsgmpkg"), FileMode.Open,
            FileAccess.Read, FileShare.None);
    }

    [Theory]
    [InlineData(PluginStopStatus.Unverified)]
    [InlineData(PluginStopStatus.Failed)]
    [InlineData((PluginStopStatus)999)]
    public async Task CompletedDeviceStopRetiresItsSlotAndAllowsAFreshCycle(PluginStopStatus stopStatus)
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        PluginHost host = new(action => action());
        var identity = new PluginInstanceIdentity(adapter.Id, "device");
        var registration = host.Admit(adapter, identity, PluginCategories.Device, PluginCategoryPolicy.Device,
            true, InitialGeneration, runtime.StateDirectory);
        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        await registration.StartAsync(deadline, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(runtime.StateDirectory, "stop-status.txt"), stopStatus.ToString());

        if (Enum.IsDefined(stopStatus))
        {
            Assert.True(await registration.StopAsync(deadline, CancellationToken.None));
            Assert.NotNull(adapter.LastState?.Reason);
            Assert.Equal(DeviceCycleState.Disabled, adapter.LastState!.State);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                registration.StopAsync(deadline, CancellationToken.None));
        }

        await registration.DisposeAsync();
        Assert.Empty(host.Snapshot());
        Assert.True(File.Exists(Path.Combine(runtime.StateDirectory, "disposed.txt")));

        File.Delete(Path.Combine(runtime.StateDirectory, "stop-status.txt"));
        var nextRuntime = await LoadRuntimeAsync(temporary, InitialGeneration + 1);
        DevicePluginCompatibilityAdapter nextAdapter = new(nextRuntime, new DeviceIdentitySnapshot(), false);
        var next = host.Admit(nextAdapter, identity, PluginCategories.Device, PluginCategoryPolicy.Device,
            true, InitialGeneration + 1, nextRuntime.StateDirectory);
        await next.StartAsync(deadline, CancellationToken.None);
        Assert.Equal(PluginHealth.Ready, Assert.Single(host.Snapshot()).Health);
        Assert.True(await next.StopAsync(deadline, CancellationToken.None));
        await next.DisposeAsync();
        Assert.Empty(host.Snapshot());
    }

    [Fact]
    public async Task CommonHostOwnsTheDeviceAdapterLifecycleAndRetiresItsVerifiedSlot()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        PluginHost host = new(action => action());
        var registration = host.Admit(adapter, new PluginInstanceIdentity(adapter.Id, "device"),
            PluginCategories.Device,
            PluginCategoryPolicy.Device, true, InitialGeneration, runtime.StateDirectory);
        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        await registration.StartAsync(deadline, CancellationToken.None);
        await host.SetModeAsync(PluginSessionMode.Game, deadline, CancellationToken.None);
        await registration.SuspendAsync(deadline, CancellationToken.None);
        await registration.ResumeAsync(InitialGeneration + 1, deadline, CancellationToken.None);
        Assert.Equal(PluginHealth.Ready, Assert.Single(host.Snapshot()).Health);
        Assert.True(await registration.StopAsync(deadline, CancellationToken.None));
        await registration.DisposeAsync();
        Assert.Empty(host.Snapshot());
        Assert.True(File.Exists(Path.Combine(runtime.StateDirectory, "disposed.txt")));
    }

    [Fact]
    public async Task CommonDeviceAdapterKeepsTheRuntimeResidentAcrossModesAndAdvancesResumeGeneration()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        await using DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        CommonHost host = new();
        var context = new PluginContext(new PluginInstanceIdentity(RuntimeFixturePlugin.PackageIdValue, "device"),
            InitialGeneration, PluginSessionMode.Desktop, Deadline.After(TimeSpan.FromSeconds(5)),
            temporary.GetPath("state"));
        Assert.Equal(PluginHealth.Ready, await adapter.StartAsync(host, context, CancellationToken.None));
        await adapter.SessionChangedAsync(context with { Mode = PluginSessionMode.Game }, CancellationToken.None);
        Assert.Equal(DeviceCycleState.Active, adapter.LastState!.State);
        await adapter.SuspendAsync(context, CancellationToken.None);
        var resumed = context with { Generation = InitialGeneration + 1 };
        await adapter.ResumeAsync(resumed, CancellationToken.None);
        Assert.Equal(InitialGeneration + 1, runtime.CycleGeneration);
        Assert.Contains(host.States,
            state => state.Generation == resumed.Generation && state.Health == PluginHealth.Ready);
        Assert.True(await adapter.StopAsync(resumed, CancellationToken.None));
    }

    [Fact]
    public async Task CommonDeviceAdapterRejectsWrongIdentityAndStaleGenerationBeforeStarting()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        await using DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        var context = new PluginContext(new PluginInstanceIdentity("other.plugin", "device"), InitialGeneration,
            PluginSessionMode.Desktop, Deadline.After(TimeSpan.FromSeconds(5)), temporary.GetPath("state"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.StartAsync(new CommonHost(), context, CancellationToken.None).AsTask());
        context = context with
        {
            Instance = new PluginInstanceIdentity(RuntimeFixturePlugin.PackageIdValue, "device"),
            Generation = InitialGeneration - 1
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.StartAsync(new CommonHost(), context, CancellationToken.None).AsTask());
        Assert.False(File.Exists(temporary.GetPath("state", RuntimeFixturePlugin.PackageIdValue, "started.txt")));
    }

    [Fact]
    public async Task ResumePublishesFreshLightingIntoTheRouterBeforeTheLifecycleCallReturns()
    {
        using TemporaryDirectory temporary = new();
        await using var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        await using DeviceCapabilityRouter router = new(action => action());
        router.Attach(runtime, InitialGeneration);
        await runtime.StartAsync(new DeviceIdentitySnapshot(), InitialGeneration, false, CancellationToken.None);
        Assert.Equal(InitialGeneration, Assert.Single(router.Snapshot()).Projection.State.CycleGeneration);

        await runtime.SuspendAsync(Deadline.After(TimeSpan.FromSeconds(1)), CancellationToken.None);
        await runtime.ResumeAsync(InitialGeneration + 1, Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        var resumed = Assert.Single(router.Snapshot());
        Assert.Equal(InitialGeneration + 1, resumed.Projection.State.CycleGeneration);
        Assert.Equal(HardwareStateQuality.Observed, resumed.Projection.State.Quality);
        Assert.True(resumed.Projection.State.Available);

        // The coordinator's post-call synchronization must not erase the accepted readback.
        router.MarkCycleGenerationChanged(InitialGeneration + 1);
        Assert.Equal(resumed.Projection.State, Assert.Single(router.Snapshot()).Projection.State);
    }

    [Fact]
    public async Task DirectLoadRunsTheLifecycleInsideTheExplicitTemporaryStateRoot()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        List<CanonicalControllerSample> samples = [];
        runtime.ControllerSampleReceived += samples.Add;

        var started = await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            InitialGeneration,
            true,
            CancellationToken.None);

        Assert.Equal(DeviceCycleState.Active, started.State);
        Assert.Equal(RuntimeFixturePlugin.DeviceDefinitionIdValue, started.DeviceDefinitionId);
        Assert.Single(samples);
        var stateDirectory = temporary.GetPath(
            "state",
            RuntimeFixturePlugin.PackageIdValue);
        Assert.True(File.Exists(Path.Combine(stateDirectory, "started.txt")));

        var suspended = await runtime.SuspendAsync(
            Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Suspended, suspended.State);

        const long resumedGeneration = InitialGeneration + 1;
        var resumed = await runtime.ResumeAsync(
            resumedGeneration,
            Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Active, resumed.State);
        Assert.Equal(2, samples.Count);

        var stopped = await runtime.StopAsync(
            PluginStopReason.IntegrationDisabled,
            Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Disabled, stopped.State);
        Assert.Contains(
            nameof(PluginStopReason.IntegrationDisabled),
            await File.ReadAllTextAsync(Path.Combine(stateDirectory, "stopped.txt")),
            StringComparison.Ordinal);

        var exit = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DeviceRuntimeExitReason.Intentional, exit.Reason);

        await runtime.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(stateDirectory, "disposed.txt")));
    }

    [Fact]
    public async Task BackgroundReportFaultCompletesTheRuntimeAndClosesCommandAdmission()
    {
        using TemporaryDirectory temporary = new();
        var release = HoldFixture();
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            var dispatched = await runtime.ExecuteCommandAsync(
                Command("fault", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, dispatched.Immediate.Outcome);
            release.SetResult();

            var exit = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceRuntimeExitReason.BackgroundFault, exit.Reason);
            Assert.Contains("background reader failed", exit.Detail, StringComparison.Ordinal);

            var refused = await runtime.ExecuteCommandAsync(
                Command("current-sample", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Rejected, refused.Immediate.Outcome);

            await runtime.StopAsync(
                PluginStopReason.RuntimeFault,
                Deadline.After(TimeSpan.FromSeconds(1)),
                CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task CanceledCommandReturnsImmediatelyAndKeepsItsLateCompletion()
    {
        using TemporaryDirectory temporary = new();
        var release = HoldFixture();
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            var command = Command(
                "late",
                InitialGeneration,
                Deadline.After(TimeSpan.FromMilliseconds(30)));

            var (immediate, late) = await runtime.ExecuteCommandAsync(
                command,
                CancellationToken.None);

            Assert.Equal(CommandOutcome.TimedOut, immediate.Outcome);
            Assert.NotNull(late);
            release.SetResult();
            var completed = await late.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(command.CommandId, completed.CommandId);
            Assert.Equal(CommandOutcome.AppliedVerified, completed.Outcome);
        }
        finally
        {
            release.TrySetResult();
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                Deadline.After(TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task EverySampleAfterAResumeIsAccepted()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        List<CanonicalControllerSample> samples = [];
        runtime.ControllerSampleReceived += samples.Add;
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            InitialGeneration,
            true,
            CancellationToken.None);
        await runtime.SuspendAsync(
            Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        const long resumedGeneration = InitialGeneration + 1;
        await runtime.ResumeAsync(
            resumedGeneration,
            Deadline.After(TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        try
        {
            // A sample carries no cycle: one the plugin read just before the wake is still the pad's
            // state. Refusing it faulted the whole cycle after every sleep.
            var acceptedBefore = samples.Count;
            var current = await runtime.ExecuteCommandAsync(
                Command("current-sample", resumedGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, current.Immediate.Outcome);
            Assert.Equal(acceptedBefore + 1, samples.Count);
        }
        finally
        {
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                Deadline.After(TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    /// <summary>Holds the fixture's late command and background fault until the test releases them.</summary>
    private static TaskCompletionSource HoldFixture()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(RuntimeFixturePlugin.ReleaseKey, release.Task);
        return release;
    }

    private static async Task<DevicePluginRuntime> StartRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        var runtime = await LoadRuntimeAsync(temporary, cycleGeneration);
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            cycleGeneration,
            true,
            CancellationToken.None);
        return runtime;
    }

    private static Task<DevicePluginRuntime> LoadRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        var sourceAssembly = typeof(RuntimeFixturePlugin).Assembly.Location;
        var entryAssembly = Path.GetFileName(sourceAssembly);
        PluginManifest manifest = new()
        {
            Id = RuntimeFixturePlugin.PackageIdValue,
            Name = "Runtime fixture",
            Version = "1.0.0",
            ApiVersion = DeviceApi.Version,
            EntryAssembly = entryAssembly,
            EntryType = typeof(RuntimeFixturePlugin).FullName!,
            WsgmVersion = PluginPackageBuilders.Host,
            Capabilities = [CapabilityRole.LightingZoneColor]
        };
        var packagePath = PluginPackageBuilders.Write(temporary.GetPath("runtime.wsgmpkg"), $$"""
              {"id":"{{manifest.Id}}","name":"{{manifest.Name}}","version":"{{manifest.Version}}",
               "apiVersion":{{manifest.ApiVersion}},"entryAssembly":"{{manifest.EntryAssembly}}",
               "entryType":"{{manifest.EntryType}}","wsgmVersion":"{{manifest.WsgmVersion}}",
               "hardware":[],"capabilities":["LightingZoneColor"]}
              """, (entryAssembly, File.ReadAllBytes(sourceAssembly)));
        InstalledDevicePackage package = new()
        {
            PackagePath = packagePath,
            Valid = true,
            Manifest = manifest
        };
        return DevicePluginRuntime.StartAsync(
            package,
            cycleGeneration,
            CancellationToken.None,
            temporary.GetPath("state"));
    }

    private static CapabilityCommand Command(
        string capabilityId,
        long cycleGeneration,
        Deadline? deadline = null)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = cycleGeneration,
            Deadline = deadline ?? Deadline.After(TimeSpan.FromSeconds(1))
        };
    }

    private sealed class CommonHost : IPluginHost
    {
        internal List<PluginHealthPublication> States { get; } = [];

        public void PublishHealth(PluginHealthPublication publication)
        {
            States.Add(publication);
        }
    }
}

/// <summary>Collectible package fixture used to exercise the production direct-plugin boundary.</summary>
public sealed class RuntimeFixturePlugin : IDevicePlugin
{
    public const string PackageIdValue = "wsgm.tests.runtime-fixture";
    public const string DeviceDefinitionIdValue = "runtime-fixture";

    /// <summary>
    ///     The AppContext entry holding the task the test completes to let a held command or fault go
    ///     on. The fixture runs in its own load context, so it shares no statics with the test.
    /// </summary>
    public const string ReleaseKey = "WSGM.Tests.RuntimeFixture.Release";

    public const string ControllerReleaseKey = "WSGM.Tests.RuntimeFixture.ControllerRelease";
    public const string StopKey = "WSGM.Tests.RuntimeFixture.Stop";

    private long _cycleGeneration;
    private IPluginHostAdapter? _host;
    private string? _stateDirectory;

    private IPluginHostAdapter Host => _host
                                       ?? throw new InvalidOperationException("The fixture plugin has not started.");

    private string StateDirectory => _stateDirectory
                                     ?? throw new InvalidOperationException("The fixture plugin has not started.");

    public string PackageId => PackageIdValue;

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = true,
            DeviceDefinitionId = DeviceDefinitionIdValue
        });
    }

    public async ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _host = context.Host;
        _cycleGeneration = context.CycleGeneration;
        _stateDirectory = context.StateDirectory;
        await File.WriteAllTextAsync(
            Path.Combine(_stateDirectory, "started.txt"),
            _cycleGeneration.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await PublishSampleAsync(cancellationToken);
        await PublishLightingAsync(cancellationToken);
        return Active();
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.CapabilityId)
        {
            case "late":
                await Released();
                break;
            case "fault":
                _ = ReportBackgroundFaultAsync();
                break;
            case "current-sample":
                await PublishSampleAsync(cancellationToken);
                break;
        }

        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public ValueTask ApplySettingsAsync(
        IReadOnlyList<DeviceSettingValue> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _cycleGeneration = context.CycleGeneration;
        await PublishSampleAsync(cancellationToken);
        await PublishLightingAsync(cancellationToken);
        return Active();
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state-directory"] = _stateDirectory ?? string.Empty
            }
        });
    }

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (AppContext.GetData(ControllerReleaseKey) is Task held)
        {
            await held;
        }
    }

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        if (AppContext.GetData(StopKey) is Task held)
        {
            await held;
            cancellationToken = CancellationToken.None;
        }

        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(
            Path.Combine(StateDirectory, "stopped.txt"),
            context.Reason.ToString(),
            cancellationToken);
        var statusPath = Path.Combine(StateDirectory, "stop-status.txt");
        var status = File.Exists(statusPath)
            ? Enum.Parse<PluginStopStatus>(await File.ReadAllTextAsync(statusPath, cancellationToken))
            : PluginStopStatus.Clean;
        return new PluginStopResult { Status = status };
    }

    public async ValueTask DisposeAsync()
    {
        if (_stateDirectory is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_stateDirectory, "disposed.txt"),
                "disposed");
        }
    }

    private async ValueTask PublishSampleAsync(CancellationToken cancellationToken)
    {
        await Host.PublishControllerSampleAsync(
            CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static Task Released()
    {
        return AppContext.GetData(ReleaseKey) as Task
               ?? throw new InvalidOperationException("The test holds nothing for the fixture.");
    }

    private async Task ReportBackgroundFaultAsync()
    {
        await Released();
        Host.ReportFault("fixture", "background reader failed");
    }

    private async ValueTask PublishLightingAsync(CancellationToken cancellationToken)
    {
        await Host.PublishDescriptorsAsync(new CapabilityDescriptorSet
        {
            CycleGeneration = _cycleGeneration,
            Generation = 1,
            Descriptors =
            [
                new CapabilityDescriptor
                {
                    CapabilityId = "lighting.zone-color",
                    Role = CapabilityRole.LightingZoneColor,
                    ValueKind = CapabilityValueKind.Color,
                    Display = new CapabilityDisplay { Key = DisplayKey.Lighting },
                    SupportsRead = true,
                    SupportsWrite = true,
                    Persistence = CapabilityPersistence.DevicePersistent
                }
            ]
        }, cancellationToken);
        await Host.PublishCapabilityStateAsync(new CapabilityState
        {
            CapabilityId = "lighting.zone-color",
            CycleGeneration = _cycleGeneration,
            DescriptorGeneration = 1,
            Available = true,
            ObservedAt = DateTimeOffset.UtcNow,
            Quality = HardwareStateQuality.Observed,
            ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0xFFFFFF }
        }, cancellationToken);
    }

    private static PluginStartResult Active()
    {
        return new PluginStartResult
        {
            State = PluginOperationalState.Active
        };
    }
}
