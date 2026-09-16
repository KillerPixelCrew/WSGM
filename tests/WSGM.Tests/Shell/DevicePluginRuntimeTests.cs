using System.Globalization;
using WSGM.Core;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Tests;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using PluginManifest = WSGM.Device.Sdk.Packaging.PluginManifest;

namespace WSGM.Tests.Shell;

public sealed class DevicePluginRuntimeTests
{
    [Fact]
    public async Task CommonHostOwnsTheDeviceAdapterLifecycleAndRetiresItsVerifiedSlot()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        PluginHost host = new(action => action());
        var registration = host.Admit(adapter, new PluginInstanceIdentity(adapter.Id, "device"), PluginCategories.Device,
            PluginCategoryPolicy.Device, true, InitialGeneration, runtime.StateDirectory);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
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
            InitialGeneration, PluginSessionMode.Desktop, DateTimeOffset.UtcNow.AddSeconds(5), temporary.GetPath("state"));
        Assert.Equal(PluginHealth.Ready, await adapter.StartAsync(host, context, CancellationToken.None));
        await adapter.SessionChangedAsync(context with { Mode = PluginSessionMode.Game }, CancellationToken.None);
        Assert.Equal(DeviceCycleState.Active, adapter.LastState!.State);
        await adapter.SuspendAsync(context, CancellationToken.None);
        var resumed = context with { Generation = InitialGeneration + 1 };
        await adapter.ResumeAsync(resumed, CancellationToken.None);
        Assert.Equal(InitialGeneration + 1, runtime.CycleGeneration);
        Assert.Contains(host.States, state => state.Generation == resumed.Generation && state.Health == PluginHealth.Ready);
        Assert.True(await adapter.StopAsync(resumed, CancellationToken.None));
    }

    [Fact]
    public async Task CommonDeviceAdapterRejectsWrongIdentityAndStaleGenerationBeforeStarting()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        await using DevicePluginCompatibilityAdapter adapter = new(runtime, new DeviceIdentitySnapshot(), false);
        var context = new PluginContext(new PluginInstanceIdentity("other.plugin", "device"), InitialGeneration,
            PluginSessionMode.Desktop, DateTimeOffset.UtcNow.AddSeconds(5), temporary.GetPath("state"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(new CommonHost(), context, CancellationToken.None).AsTask());
        context = context with { Instance = new PluginInstanceIdentity(RuntimeFixturePlugin.PackageIdValue, "device"), Generation = InitialGeneration - 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(new CommonHost(), context, CancellationToken.None).AsTask());
        Assert.False(File.Exists(temporary.GetPath("state", RuntimeFixturePlugin.PackageIdValue, "started.txt")));
    }

    private sealed class CommonHost : IPluginHost
    {
        internal List<PluginHealthPublication> States { get; } = [];
        public void PublishHealth(PluginHealthPublication publication) => States.Add(publication);
    }

    private const long InitialGeneration = 41;

    [Fact]
    public async Task ResumePublishesFreshLightingIntoTheRouterBeforeTheLifecycleCallReturns()
    {
        using TemporaryDirectory temporary = new();
        await using var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        await using DeviceCapabilityRouter router = new(action => action());
        router.Attach(runtime, InitialGeneration);
        await runtime.StartAsync(new DeviceIdentitySnapshot(), InitialGeneration, false, CancellationToken.None);
        Assert.Equal(InitialGeneration, Assert.Single(router.Snapshot()).Projection.State.CycleGeneration);

        await runtime.SuspendAsync(DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
        await runtime.ResumeAsync(InitialGeneration + 1, DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
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
            controllerManagementEnabled: true,
            CancellationToken.None);

        Assert.Equal(DeviceCycleState.Active, started.State);
        Assert.Equal(RuntimeFixturePlugin.DeviceDefinitionIdValue, started.DeviceDefinitionId);
        Assert.Equal(InitialGeneration, Assert.Single(samples).CycleGeneration);
        var stateDirectory = temporary.GetPath(
            "state",
            RuntimeFixturePlugin.PackageIdValue);
        Assert.True(File.Exists(Path.Combine(stateDirectory, "started.txt")));

        var suspended = await runtime.SuspendAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Suspended, suspended.State);

        const long resumedGeneration = InitialGeneration + 1;
        var resumed = await runtime.ResumeAsync(
            resumedGeneration,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Active, resumed.State);
        Assert.Equal(resumedGeneration, samples[^1].CycleGeneration);

        var stopped = await runtime.StopAsync(
            PluginStopReason.IntegrationDisabled,
            DateTimeOffset.UtcNow.AddSeconds(1),
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
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            var dispatched = await runtime.ExecuteCommandAsync(
                Command("fault", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, dispatched.Immediate.Outcome);

            var exit = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceRuntimeExitReason.BackgroundFault, exit.Reason);
            Assert.Contains("background reader failed", exit.Detail, StringComparison.Ordinal);

            var refused = await runtime.ExecuteCommandAsync(
                Command("current-sample", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Rejected, refused.Immediate.Outcome);

            await runtime.StopAsync(
                PluginStopReason.RuntimeFault,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task CanceledCommandReturnsImmediatelyAndKeepsItsLateCompletion()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            var command = Command(
                "late",
                InitialGeneration,
                DateTimeOffset.UtcNow.AddMilliseconds(30));

            var (immediate, late) = await runtime.ExecuteCommandAsync(
                command,
                CancellationToken.None);

            Assert.Equal(CommandOutcome.TimedOut, immediate.Outcome);
            Assert.NotNull(late);
            var completed = await late.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(command.CommandId, completed.CommandId);
            Assert.Equal(CommandOutcome.AppliedVerified, completed.Outcome);
        }
        finally
        {
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task FreshGenerationAcceptsCurrentSamplesAndRejectsStaleSamples()
    {
        using TemporaryDirectory temporary = new();
        var runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        List<CanonicalControllerSample> samples = [];
        runtime.ControllerSampleReceived += samples.Add;
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            InitialGeneration,
            controllerManagementEnabled: true,
            CancellationToken.None);
        await runtime.SuspendAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        const long resumedGeneration = InitialGeneration + 1;
        await runtime.ResumeAsync(
            resumedGeneration,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);

        try
        {
            var acceptedBeforeStale = samples.Count;
            var stale = await runtime.ExecuteCommandAsync(
                Command("stale-sample", resumedGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Indeterminate, stale.Immediate.Outcome);
            Assert.Equal(acceptedBeforeStale, samples.Count);

            var current = await runtime.ExecuteCommandAsync(
                Command("current-sample", resumedGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, current.Immediate.Outcome);
            Assert.Equal(acceptedBeforeStale + 1, samples.Count);
            Assert.Equal(resumedGeneration, samples[^1].CycleGeneration);
        }
        finally
        {
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    private static async Task<DevicePluginRuntime> StartRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        var runtime = await LoadRuntimeAsync(temporary, cycleGeneration);
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            cycleGeneration,
            controllerManagementEnabled: true,
            CancellationToken.None);
        return runtime;
    }

    private static Task<DevicePluginRuntime> LoadRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        var packageDirectory = temporary.GetPath("package");
        Directory.CreateDirectory(packageDirectory);
        var sourceAssembly = typeof(RuntimeFixturePlugin).Assembly.Location;
        var entryAssembly = Path.GetFileName(sourceAssembly);
        File.Copy(sourceAssembly, Path.Combine(packageDirectory, entryAssembly));
        InstalledDevicePackage package = new()
        {
            PackagePath = packageDirectory,
            Valid = true,
            Manifest = new PluginManifest
            {
                Id = RuntimeFixturePlugin.PackageIdValue,
                Name = "Runtime fixture",
                Version = "1.0.0",
                ApiVersion = DeviceApi.Version,
                EntryAssembly = entryAssembly,
                EntryType = typeof(RuntimeFixturePlugin).FullName!
            }
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
        DateTimeOffset? deadline = null) => new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = cycleGeneration,
            Deadline = deadline ?? DateTimeOffset.UtcNow.AddSeconds(1)
        };
}

/// <summary>Collectible package fixture used to exercise the production direct-plugin boundary.</summary>
public sealed class RuntimeFixturePlugin : IDevicePlugin
{
    public const string PackageIdValue = "wsgm.tests.runtime-fixture";
    public const string DeviceDefinitionIdValue = "runtime-fixture";
    private IPluginHostAdapter? _host;
    private string? _stateDirectory;
    private long _cycleGeneration;
    private long _sequence;

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
        await PublishSampleAsync(_cycleGeneration, cancellationToken);
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
                await Task.Delay(120, CancellationToken.None);
                break;
            case "fault":
                _ = ReportBackgroundFaultAsync();
                break;
            case "stale-sample":
                await PublishSampleAsync(_cycleGeneration - 1, cancellationToken);
                break;
            case "current-sample":
                await PublishSampleAsync(_cycleGeneration, cancellationToken);
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
        await PublishSampleAsync(_cycleGeneration, cancellationToken);
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
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified
        });
    }

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Enabled)
        {
            _cycleGeneration = context.CycleGeneration;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(
            Path.Combine(StateDirectory, "stopped.txt"),
            context.Reason.ToString(),
            cancellationToken);
        return new PluginStopResult { Status = PluginStopStatus.Clean };
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

    private IPluginHostAdapter Host => _host
        ?? throw new InvalidOperationException("The fixture plugin has not started.");

    private string StateDirectory => _stateDirectory
        ?? throw new InvalidOperationException("The fixture plugin has not started.");

    private async ValueTask PublishSampleAsync(
        long cycleGeneration,
        CancellationToken cancellationToken)
    {
        await Host.PublishControllerSampleAsync(new CanonicalControllerSample
        {
            Sequence = Interlocked.Increment(ref _sequence),
            CycleGeneration = cycleGeneration,
            Timestamp = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task ReportBackgroundFaultAsync()
    {
        await Task.Delay(20);
        Host.ReportFault("fixture", "background reader failed");
    }

    private async ValueTask PublishLightingAsync(CancellationToken cancellationToken)
    {
        await Host.PublishDescriptorsAsync(new CapabilityDescriptorSet
        {
            CycleGeneration = _cycleGeneration,
            Generation = 1,
            Descriptors = [new CapabilityDescriptor
            {
                CapabilityId = "lighting.zone-color",
                Role = CapabilityRole.LightingZoneColor,
                ValueKind = CapabilityValueKind.Color,
                Display = new CapabilityDisplay { Key = DisplayKey.Lighting },
                SupportsRead = true,
                SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            }]
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

    private static PluginStartResult Active() => new()
    {
        State = PluginOperationalState.Active
    };
}
