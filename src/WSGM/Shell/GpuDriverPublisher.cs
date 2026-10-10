using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Projects one directly owned native GPU stream into the application capability router.</summary>
internal sealed class GpuDriverPublisher : ICapabilityPublisher, IBuiltinGpuDriver
{
    private readonly GpuCoordinator _coordinator;
    private readonly GpuDriver _driver;
    private readonly Lock _gate = new();
    private readonly Dictionary<DeviceCapabilityKey, CapabilityState> _states = [];
    private bool _closed;
    private CapabilityDescriptorSet _descriptors = new() { Descriptors = [] };
    private IReadOnlyDictionary<string, GpuDisplayTarget> _displayTargets = new Dictionary<string, GpuDisplayTarget>();
    private IReadOnlyList<CapabilityRole> _roles = [];
    private long _sequence;
    private GpuStatus _status = new(GpuHealth.Unavailable, "The driver has not started.");
    private bool _suspended;

    internal GpuDriverPublisher(GpuCoordinator coordinator, BuiltinGpuDriver definition, string stateDirectory)
    {
        _coordinator = coordinator;
        Definition = definition;
        _driver = GpuDriver.Create(definition.Vendor, stateDirectory, entry =>
        {
            var text = $"Graphics/{definition.Name}/{entry.Scope}: {entry.Message}";
            switch (entry.Level)
            {
                case GpuLogLevel.Error: Log.Error(text); break;
                case GpuLogLevel.Warn: Log.Warn(text); break;
                case GpuLogLevel.Info: Log.Info(text); break;
                default: throw new ArgumentOutOfRangeException(nameof(entry));
            }
        });
        _driver.DescriptorsChanged += OnDescriptors;
        _driver.StateChanged += OnState;
        _driver.StatusChanged += OnStatus;
        _coordinator.OpenBuiltin(this);
    }

    internal BuiltinGpuDriver Definition { get; }

    /// <summary>Reads physical output identities from the current driver descriptor generation.</summary>
    internal IReadOnlyDictionary<string, GpuDisplayTarget> DisplayTargets
    {
        get
        {
            lock (_gate)
            {
                return _displayTargets;
            }
        }
    }

    internal GpuStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        CloseAdmission();
        try
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _driver.DescriptorsChanged -= OnDescriptors;
            _driver.StateChanged -= OnState;
            _driver.StatusChanged -= OnStatus;
            _coordinator.CloseBuiltin(this);
        }
    }

    public ValueTask<GpuHealth> StartAsync(CancellationToken token)
    {
        return _driver.StartAsync(token);
    }

    public ValueTask RefreshTopologyAsync(CancellationToken token)
    {
        return _driver.RefreshTopologyAsync(token);
    }

    public void CloseAdmission()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    public void SetSuspended(bool suspended)
    {
        CapabilityDescriptorSet descriptors;
        CapabilityState[] states;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _suspended = suspended;
            descriptors = _descriptors;
            states = _states.Values.ToArray();
        }

        if (!suspended)
        {
            DescriptorSetReceived?.Invoke(descriptors);
            foreach (var state in states)
            {
                CapabilityStateReceived?.Invoke(new CapabilityStateDelta(
                    Interlocked.Increment(ref _sequence), state));
            }
        }

        _coordinator.BuiltinChanged();
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_closed && !_suspended;
            }
        }
    }

    public IReadOnlyList<CapabilityRole> DeclaredCapabilities
    {
        get
        {
            lock (_gate)
            {
                return _roles;
            }
        }
    }

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;
    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public async Task<DeviceCommandDispatch> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        using var wait = command.Deadline.CreateCancellationSource(cancellationToken);
        if (!IsActive || wait.IsCancellationRequested)
        {
            return new DeviceCommandDispatch(Result(command, CommandOutcome.Rejected,
                new CapabilityReason(CapabilityReasonCode.Quiescing,
                    "The graphics driver is not admitting commands.")));
        }

        var work = ExecuteOwnedAsync(command, wait.Token);
        try
        {
            return new DeviceCommandDispatch(await work.WaitAsync(wait.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return work.IsCompleted
                ? new DeviceCommandDispatch(await work.ConfigureAwait(false))
                : new DeviceCommandDispatch(Result(command, CommandOutcome.Indeterminate,
                    new CapabilityReason(CapabilityReasonCode.Quiescing,
                        "The caller stopped waiting; the graphics driver still owns the command.")), work);
        }
    }

    private async Task<CapabilityCommandResult> ExecuteOwnedAsync(CapabilityCommand command, CancellationToken token)
    {
        var result = await _driver.ExecuteAsync(new GpuCommand
        {
            CapabilityId = command.CapabilityId, InstanceId = command.InstanceId,
            RequestedValue = command.RequestedValue is null ? null : ToLibrary(command.RequestedValue)
        }, token).ConfigureAwait(false);
        return Result(command, result.Outcome switch
            {
                GpuCommandOutcome.AppliedVerified => CommandOutcome.AppliedVerified,
                GpuCommandOutcome.AppliedUnverified => CommandOutcome.AppliedUnverified,
                GpuCommandOutcome.Rejected => CommandOutcome.Rejected,
                GpuCommandOutcome.Indeterminate => CommandOutcome.Indeterminate,
                _ => throw new ArgumentOutOfRangeException(nameof(result))
            }, result.Reason is null ? null : ToHost(result.Reason),
            result.ReadbackValue is null ? null : ToHost(result.ReadbackValue));
    }

    internal async ValueTask<ApplicationProfileSyncResult?> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        if (!IsActive)
        {
            return null;
        }

        var work = _driver.SyncApplicationProfilesAsync(new GpuProfileSync(sync.Revision,
            sync.Profiles.Select(profile => new GpuApplicationProfile(profile.ProfileId, profile.DisplayName,
                profile.Executables, profile.Values.Select(value => new GpuApplicationValue(value.CapabilityId,
                    value.InstanceId, ToLibrary(value.Value))).ToArray())).ToArray()), cancellationToken).AsTask();
        GpuProfileSyncResult result;
        try
        {
            result = await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The library's lane still owns the native work and retirement waits for it.
            Log.Observe(work, "Late graphics application profile synchronization", true);
            throw;
        }

        return new ApplicationProfileSyncResult(result.Written, result.Removed,
            result.Failures.Select(failure => new ApplicationProfileFailure(failure.ProfileId, failure.Executable,
                failure.CapabilityId, failure.Detail)).ToArray());
    }

    private void OnStatus(GpuStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        _coordinator.BuiltinChanged();
    }

    private void OnDescriptors(GpuDescriptorSet set)
    {
        var descriptors = ToHost(set);
        lock (_gate)
        {
            _descriptors = descriptors;
            _displayTargets = set.Sections.Where(section => section.DisplayTarget is not null)
                .ToDictionary(section => section.SectionId, section => section.DisplayTarget!, StringComparer.Ordinal);
            _roles = descriptors.Descriptors.Select(descriptor => descriptor.Role).Distinct().ToArray();
            var keys = descriptors.Descriptors.Select(descriptor => new DeviceCapabilityKey(
                descriptor.CapabilityId, descriptor.InstanceId)).ToHashSet();
            foreach (var key in _states.Keys.Where(key => !keys.Contains(key)).ToArray())
            {
                _states.Remove(key);
            }

            if (_closed || _suspended)
            {
                return;
            }
        }

        DescriptorSetReceived?.Invoke(descriptors);
    }

    private void OnState(GpuState state)
    {
        var observed = new CapabilityState
        {
            CapabilityId = state.CapabilityId, InstanceId = state.InstanceId,
            ObservedValue = state.ObservedValue is null ? null : ToHost(state.ObservedValue),
            Available = state.Available, ObservedAt = state.ObservedAt,
            Reason = state.Reason is null ? null : ToHost(state.Reason), Quality = state.Quality switch
            {
                GpuStateQuality.Unknown => HardwareStateQuality.Unknown,
                GpuStateQuality.Observed => HardwareStateQuality.Observed,
                GpuStateQuality.Verified => HardwareStateQuality.Verified,
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            }
        };
        lock (_gate)
        {
            _states[new DeviceCapabilityKey(state.CapabilityId, state.InstanceId)] = observed;
            if (_closed || _suspended)
            {
                return;
            }
        }

        CapabilityStateReceived?.Invoke(new CapabilityStateDelta(Interlocked.Increment(ref _sequence), observed));
    }

    internal static CapabilityDescriptorSet ToHost(GpuDescriptorSet set)
    {
        return new CapabilityDescriptorSet
        {
            Descriptors = set.Descriptors.Select(ToHost).ToArray(),
            Sections = set.Sections.Select(section => new CapabilitySection
            {
                SectionId = section.SectionId, Key = SettingSectionKey.Custom, CustomTitle = section.Title,
                CustomDescription = section.Description, Icon = section.Kind switch
                {
                    GpuSectionKind.Display => SectionIcon.Display,
                    GpuSectionKind.Adapter => SectionIcon.Wrench,
                    _ => throw new ArgumentOutOfRangeException(nameof(section))
                },
                SortOrder = section.SortOrder,
                Categories = section.Categories.Select(category => new CapabilityCategory
                {
                    CategoryId = category.CategoryId, Key = SettingSectionKey.Custom,
                    CustomTitle = category.Title, SortOrder = category.SortOrder
                }).ToArray()
            }).ToArray()
        };
    }

    private static CapabilityDescriptor ToHost(GpuDescriptor descriptor)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId, Role = Role(descriptor.Role),
            ValueKind = descriptor.ValueKind switch
            {
                GpuValueKind.None => CapabilityValueKind.None,
                GpuValueKind.Boolean => CapabilityValueKind.Boolean,
                GpuValueKind.Integer => CapabilityValueKind.Integer,
                GpuValueKind.Choice => CapabilityValueKind.Choice,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = descriptor.Label },
            SectionId = descriptor.SectionId, CategoryId = descriptor.CategoryId, SortOrder = descriptor.SortOrder,
            SupportsRead = descriptor.SupportsRead, SupportsWrite = descriptor.SupportsWrite,
            SupportsAction = descriptor.Role == GpuRole.Action,
            Minimum = descriptor.Minimum, Maximum = descriptor.Maximum, Step = descriptor.Step,
            Unit = descriptor.Unit switch
            {
                GpuUnit.None => CapabilityUnit.None,
                GpuUnit.Percent => CapabilityUnit.Percent,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            Persistence = descriptor.Persistence switch
            {
                GpuPersistence.DevicePersistent => CapabilityPersistence.DevicePersistent,
                GpuPersistence.Volatile => CapabilityPersistence.Volatile,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            ProfileScope = descriptor.ProfileScope switch
            {
                GpuProfileScope.GlobalOnly => CapabilityProfileScope.GlobalOnly,
                GpuProfileScope.Switched => CapabilityProfileScope.Switched,
                GpuProfileScope.NativePerApplication => CapabilityProfileScope.NativePerApplication,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            ApplyTiming = descriptor.ApplyTiming switch
            {
                GpuApplyTiming.Immediate => CapabilityApplyTiming.Immediate,
                GpuApplyTiming.NextApplicationStart => CapabilityApplyTiming.NextApplicationStart,
                GpuApplyTiming.SystemRestart => CapabilityApplyTiming.SystemRestart,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            Choices = descriptor.Choices.Select(choice => new CapabilityChoice(choice.Value,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice.Label })).ToArray()
        };
    }

    private static CapabilityRole Role(GpuRole role)
    {
        return role switch
        {
            GpuRole.Toggle => CapabilityRole.GenericToggle,
            GpuRole.Choice => CapabilityRole.GenericChoice,
            GpuRole.Range => CapabilityRole.GenericRange,
            GpuRole.ReadOnly => CapabilityRole.GenericReadOnly,
            GpuRole.Action => CapabilityRole.GenericAction,
            GpuRole.VariableRefreshRate => CapabilityRole.VariableRefreshRate,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
    }

    private static CapabilityReason ToHost(GpuReason reason)
    {
        return new CapabilityReason(reason.Code switch
        {
            GpuReasonCode.DriverUnavailable => CapabilityReasonCode.HostUnavailable,
            GpuReasonCode.PrerequisiteMissing => CapabilityReasonCode.PrerequisiteMissing,
            GpuReasonCode.Quiescing => CapabilityReasonCode.Quiescing,
            GpuReasonCode.TransportFaulted => CapabilityReasonCode.TransportFaulted,
            GpuReasonCode.Unsupported => CapabilityReasonCode.Unsupported,
            GpuReasonCode.ValueOutOfRange => CapabilityReasonCode.ValueOutOfRange,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        }, reason.Detail, reason.Retryable);
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

    private static CapabilityCommandResult Result(CapabilityCommand command, CommandOutcome outcome,
        CapabilityReason? reason = null, CapabilityValue? readback = null)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId, Outcome = outcome, Reason = reason, ReadbackValue = readback,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
