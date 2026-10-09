using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Projects one library driver into the application's existing capability/profile router.</summary>
internal sealed class GpuDriverAdapter : ICapabilityPlugin, IAsyncDisposable
{
    private readonly PluginCapabilityChannel _channel;
    private readonly GpuCoordinator _coordinator;
    private readonly GpuDriver _driver;
    private readonly PluginInstanceIdentity _identity;

    internal GpuDriverAdapter(GpuCoordinator coordinator, BuiltinGpuDriver definition,
        PluginInstanceIdentity identity, string stateDirectory)
    {
        _coordinator = coordinator;
        _identity = identity;
        _driver = GpuDriver.Create(definition.Vendor, stateDirectory, entry =>
        {
            var text = $"Graphics/{definition.Name}/{entry.Scope}: {entry.Message}";
            switch (entry.Level)
            {
                case GpuLogLevel.Error: Log.Error(text); break;
                case GpuLogLevel.Warn: Log.Warn(text); break;
                default: Log.Info(text); break;
            }
        });
        _channel = coordinator.OpenBuiltin(identity, definition.Name, this);
        _driver.CycleStarted += OnCycle;
        _driver.AdmissionClosed += OnAdmissionClosed;
        _driver.DescriptorsChanged += OnDescriptors;
        _driver.StateChanged += OnState;
        _driver.StatusChanged += OnStatus;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Close();
        await _driver.DisposeAsync().ConfigureAwait(false);
        _driver.CycleStarted -= OnCycle;
        _driver.AdmissionClosed -= OnAdmissionClosed;
        _driver.DescriptorsChanged -= OnDescriptors;
        _driver.StateChanged -= OnState;
        _driver.StatusChanged -= OnStatus;
        _coordinator.Close(_channel);
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _driver.ExecuteAsync(new GpuCommand
        {
            CommandId = command.CommandId,
            CapabilityId = command.CapabilityId,
            InstanceId = command.InstanceId,
            ExpectedCycleGeneration = command.ExpectedCycleGeneration,
            ExpectedDescriptorGeneration = command.ExpectedDescriptorGeneration,
            RequestedValue = command.RequestedValue is null ? null : ToLibrary(command.RequestedValue),
            Deadline = command.Deadline.HasExpired
                ? DriverDeadline.Expired
                : DriverDeadline.After(command.Deadline.Remaining)
        }, cancellationToken).ConfigureAwait(false);
        return new CapabilityCommandResult
        {
            CommandId = result.CommandId,
            Outcome = Enum.Parse<CommandOutcome>(result.Outcome.ToString()),
            Reason = result.Reason is null ? null : ToHost(result.Reason),
            ReadbackValue = result.ReadbackValue is null ? null : ToHost(result.ReadbackValue),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public async ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        var result = await _driver.SyncApplicationProfilesAsync(new GpuProfileSync(sync.Revision,
            sync.CycleGeneration, sync.Profiles.Select(profile => new GpuApplicationProfile(profile.ProfileId,
                profile.DisplayName, profile.Executables,
                profile.Values.Select(value => new GpuApplicationValue(value.CapabilityId, value.InstanceId,
                    ToLibrary(value.Value))).ToArray())).ToArray()), cancellationToken).ConfigureAwait(false);
        return new ApplicationProfileSyncResult(result.Written, result.Removed,
            result.Failures.Select(failure => new ApplicationProfileFailure(failure.ProfileId, failure.Executable,
                failure.CapabilityId, failure.Detail)).ToArray());
    }

    internal ValueTask<GpuHealth> StartAsync(CancellationToken token)
    {
        return _driver.StartAsync(token);
    }

    internal ValueTask SuspendAsync(CancellationToken token)
    {
        return _driver.SuspendAsync(token);
    }

    internal ValueTask ResumeAsync(CancellationToken token)
    {
        return _driver.ResumeAsync(token);
    }

    internal void CloseAdmission()
    {
        _channel.Suspend();
        _driver.StopAsync(new CancellationToken(true)).AsTask().ObserveFaults();
    }

    internal ValueTask<bool> StopAsync(CancellationToken token)
    {
        return _driver.StopAsync(token);
    }

    private void OnCycle(long generation)
    {
        _channel.BeginCycle(generation);
    }

    private void OnAdmissionClosed(long _)
    {
        _channel.Suspend();
    }

    private void OnStatus(GpuStatus status)
    {
        _coordinator.ReportBuiltinHealth(new PluginHealthPublication(
            _identity, status.CycleGeneration, Enum.Parse<PluginHealth>(status.Health.ToString()), status.Detail));
    }

    private void OnDescriptors(GpuDescriptorSet set)
    {
        _channel.PublishDescriptorsAsync(new CapabilityDescriptorSet
        {
            Generation = set.Generation,
            CycleGeneration = set.CycleGeneration,
            Descriptors = set.Descriptors.Select(ToHost).ToArray(),
            Sections = set.Sections.Select(section => new CapabilitySection
            {
                SectionId = section.SectionId,
                Key = SettingSectionKey.Custom,
                CustomTitle = section.Title,
                CustomDescription = section.Description,
                Icon = section.Kind == GpuSectionKind.Display ? SectionIcon.Display : SectionIcon.Gauge,
                SortOrder = section.SortOrder,
                Categories = section.Categories.Select(category => new CapabilityCategory
                {
                    CategoryId = category.CategoryId,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = category.Title,
                    SortOrder = category.SortOrder
                }).ToArray()
            }).ToArray()
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void OnState(GpuState state)
    {
        _channel.PublishCapabilityStateAsync(new CapabilityState
        {
            CapabilityId = state.CapabilityId,
            InstanceId = state.InstanceId,
            CycleGeneration = state.CycleGeneration,
            DescriptorGeneration = state.DescriptorGeneration,
            ObservedValue = state.ObservedValue is null ? null : ToHost(state.ObservedValue),
            Available = state.Available,
            ObservedAt = state.ObservedAt,
            Reason = state.Reason is null ? null : ToHost(state.Reason),
            Quality = Enum.Parse<HardwareStateQuality>(state.Quality.ToString())
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static CapabilityDescriptor ToHost(GpuDescriptor descriptor)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Role = descriptor.Role switch
            {
                GpuRole.Toggle => CapabilityRole.GenericToggle,
                GpuRole.Choice => CapabilityRole.GenericChoice,
                GpuRole.Range => CapabilityRole.GenericRange,
                GpuRole.ReadOnly => CapabilityRole.GenericReadOnly,
                GpuRole.Action => CapabilityRole.GenericAction,
                GpuRole.VariableRefreshRate => CapabilityRole.VariableRefreshRate,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            ValueKind = Enum.Parse<CapabilityValueKind>(descriptor.ValueKind.ToString()),
            Display = new CapabilityDisplay
            {
                Key = descriptor.Role == GpuRole.VariableRefreshRate
                    ? DisplayKey.VariableRefreshRate
                    : DisplayKey.Custom,
                CustomLabel = descriptor.Label
            },
            SectionId = descriptor.SectionId,
            CategoryId = descriptor.CategoryId,
            SortOrder = descriptor.SortOrder,
            SupportsRead = descriptor.SupportsRead,
            SupportsWrite = descriptor.SupportsWrite,
            SupportsAction = descriptor.SupportsAction,
            Minimum = descriptor.Minimum,
            Maximum = descriptor.Maximum,
            Step = descriptor.Step,
            Unit = Enum.Parse<CapabilityUnit>(descriptor.Unit.ToString()),
            Persistence = Enum.Parse<CapabilityPersistence>(descriptor.Persistence.ToString()),
            ProfileScope = Enum.Parse<CapabilityProfileScope>(descriptor.ProfileScope.ToString()),
            ApplyTiming = Enum.Parse<CapabilityApplyTiming>(descriptor.ApplyTiming.ToString()),
            Choices = descriptor.Choices.Select(choice => new CapabilityChoice(choice.Value,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice.Label })).ToArray()
        };
    }

    private static CapabilityReason ToHost(GpuReason reason)
    {
        return new CapabilityReason(
            reason.Code == GpuReasonCode.DriverUnavailable
                ? CapabilityReasonCode.HostUnavailable
                : Enum.Parse<CapabilityReasonCode>(reason.Code.ToString()), reason.Detail, reason.Retryable);
    }

    private static GpuValue ToLibrary(CapabilityValue value)
    {
        return value.Kind switch
        {
            CapabilityValueKind.Boolean => GpuValue.Boolean(value.BooleanValue!.Value),
            CapabilityValueKind.Integer => GpuValue.Integer(value.IntegerValue!.Value),
            CapabilityValueKind.Choice => GpuValue.Choice(value.ChoiceValue!),
            CapabilityValueKind.None => GpuValue.None(),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static CapabilityValue ToHost(GpuValue value)
    {
        return value.Kind switch
        {
            GpuValueKind.Boolean => CapabilityValue.Boolean(value.BooleanValue!.Value),
            GpuValueKind.Integer => CapabilityValue.Integer(value.IntegerValue!.Value),
            GpuValueKind.Choice => CapabilityValue.Choice(value.ChoiceValue!),
            GpuValueKind.None => CapabilityValue.None(),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }
}
