using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using P = LibHandheld.Contracts;

namespace WSGM.Shell;

/// <summary>Observes one native handheld. The library serializes its lifecycle and hardware operations.</summary>
internal sealed class HandheldDeviceRuntime : IAsyncDisposable, ICapabilityPublisher, P.IHandheldObserver
{
    private readonly TaskCompletionSource<DeviceRuntimeExit> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private readonly Lock _workGate = new();
    private Task? _dispose;
    private bool _disposed;
    private bool _hasSoftwareFanCurve;
    private int? _softwareChargeLimit;
    private IReadOnlyList<CurvePoint> _softwareFanCurve = [];
    private string? _softwareFanReason;
    private volatile DeviceCycleState _state = DeviceCycleState.Disabled;
    private long _stateSequence;
    private Task<HandheldRuntimeState>? _stop;

    internal HandheldDeviceRuntime(HandheldDevice device, string stateDirectory)
    {
        Device = device;
        StateDirectory = stateDirectory;
        device.Observer = this;
    }

    internal Func<IReadOnlyList<CurvePoint>, CancellationToken, Task>? ApplySoftwareFanCurveAsync { get; set; }
    internal Func<int, CancellationToken, Task>? ApplySoftwareChargeLimitAsync { get; set; }
    internal bool UsesSoftwareChargeLimit { get; private set; }

    internal string StateDirectory { get; }
    internal HandheldDevice Device { get; }

    internal DeviceCycleState LifecycleState => _state;
    internal Task<DeviceRuntimeExit> Completion => _completion.Task;
    internal Task LateCleanup { get; private set; } = Task.CompletedTask;

    private bool AcceptsPublications => !_disposed &&
                                        _state is DeviceCycleState.Activating or DeviceCycleState.Active
                                            or DeviceCycleState.Degraded;

    internal static string SourceVersion => typeof(HandheldDevice).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public ValueTask DisposeAsync()
    {
        return DisposeAsync(Deadline.After(TimeSpan.FromSeconds(5)));
    }

    public bool IsActive => !_disposed && _state is DeviceCycleState.Active or DeviceCycleState.Degraded;

    public IReadOnlyList<CapabilityRole> DeclaredCapabilities => Device.Definition.DeclaredRoles
        .Select(role => (CapabilityRole)role).Append(CapabilityRole.FanCurve).Append(CapabilityRole.ChargeLimit)
        .Distinct().ToArray();

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;
    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public async Task<DeviceCommandDispatch> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        if (!IsActive || token.IsCancellationRequested || command.Deadline.HasExpired)
        {
            return new DeviceCommandDispatch(Rejected(command, "Inactive or cancelled before native dispatch."));
        }

        using var bounded = command.Deadline.CreateCancellationSource(token, _lifetime.Token);
        var task = DispatchAsync(command, bounded.Token);
        Track(task);
        try
        {
            return new DeviceCommandDispatch(await task.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DeviceCommandDispatch(Result(command, CommandOutcome.Indeterminate, exception.Message));
        }
    }

    ValueTask P.IHandheldObserver.DescriptorsChangedAsync(P.CapabilityDescriptorSet descriptors,
        CancellationToken token)
    {
        if (AcceptsPublications)
        {
            var projected = HandheldUiProjection.Descriptors(descriptors);
            _hasSoftwareFanCurve = ApplySoftwareFanCurveAsync is not null
                                   && descriptors.Descriptors.Any(item =>
                                       item.Role == P.CapabilityRole.FanDuty && item.SupportsWrite);
            if (_hasSoftwareFanCurve)
            {
                projected = projected with
                {
                    Descriptors =
                    [
                        .. projected.Descriptors.Where(item =>
                            item.Role != CapabilityRole.FanCurve || item.InstanceId == "xg"),
                        new CapabilityDescriptor
                        {
                            CapabilityId = CapabilityIds.FanCurve, Role = CapabilityRole.FanCurve,
                            ValueKind = CapabilityValueKind.Curve,
                            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
                            SupportsRead = true, SupportsWrite = true, Minimum = 0, Maximum = 100, Step = 1,
                            Unit = CapabilityUnit.Percent, Persistence = CapabilityPersistence.Volatile,
                            SectionId = DeviceSections.PowerId, CategoryId = "fans", SortOrder = 1
                        }
                    ]
                };
            }

            UsesSoftwareChargeLimit = ApplySoftwareChargeLimitAsync is not null
                                      && !descriptors.Descriptors.Any(item => item.Role == P.CapabilityRole.ChargeLimit)
                                      && descriptors.Descriptors.Any(item => item.Role == P.CapabilityRole.ChargeBypass
                                                                             && item.ValueKind ==
                                                                             P.CapabilityValueKind.Boolean &&
                                                                             item.SupportsWrite);
            if (UsesSoftwareChargeLimit)
            {
                projected = projected with
                {
                    Descriptors =
                    [
                        .. projected.Descriptors.Where(item => item.Role != CapabilityRole.ChargeBypass),
                        new CapabilityDescriptor
                        {
                            CapabilityId = CapabilityIds.ChargeLimit, Role = CapabilityRole.ChargeLimit,
                            ValueKind = CapabilityValueKind.Integer,
                            Display = new CapabilityDisplay { Key = DisplayKey.ChargeLimit },
                            SupportsRead = true, SupportsWrite = true, Minimum = 1, Maximum = 100, Step = 1,
                            Unit = CapabilityUnit.Percent, Persistence = CapabilityPersistence.Volatile,
                            SectionId = DeviceSections.PowerId, CategoryId = "charging"
                        }
                    ]
                };
            }

            DescriptorSetReceived?.Invoke(projected);
            PublishSoftwareFanCurve();
            PublishSoftwareChargeLimit();
        }

        return ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.StateChangedAsync(P.CapabilityState state, CancellationToken token)
    {
        if (!_disposed && (AcceptsPublications || !state.Available))
        {
            if ((!_hasSoftwareFanCurve || state.CapabilityId != CapabilityIds.FanCurve)
                && (!UsesSoftwareChargeLimit || !Device.Controls.Controls.Any(item =>
                    item.Role == P.CapabilityRole.ChargeBypass && item.Descriptor.CapabilityId == state.CapabilityId)))
            {
                CapabilityStateReceived?.Invoke(new CapabilityStateDelta(Interlocked.Increment(ref _stateSequence),
                    HandheldUiProjection.State(state)));
            }

            if (_hasSoftwareFanCurve && Device.Controls.Controls.Any(item =>
                    item.Role == P.CapabilityRole.FanDuty && item.Descriptor.CapabilityId == state.CapabilityId))
            {
                PublishSoftwareFanCurve();
            }

            if (UsesSoftwareChargeLimit)
            {
                PublishSoftwareChargeLimit();
            }
        }

        return ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.PhysicalDevicesChangedAsync(IReadOnlyList<P.PhysicalDeviceIdentity> devices,
        P.HapticCapabilities? output, CancellationToken token)
    {
        if (!_disposed && (AcceptsPublications || devices.Count == 0))
        {
            PhysicalIdentitiesReceived?.Invoke((devices, output));
        }

        return ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.ControllerSampleAsync(P.CanonicalControllerSample sample, CancellationToken token)
    {
        if (IsActive)
        {
            ControllerSampleReceived?.Invoke(sample);
        }

        return ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.OemControlsChangedAsync(IReadOnlyList<P.OemControlDescriptor> controls,
        CancellationToken token)
    {
        if (!_disposed && (AcceptsPublications || controls.Count == 0))
        {
            OemControlsReceived?.Invoke(controls);
        }

        return ValueTask.CompletedTask;
    }

    ValueTask P.IHandheldObserver.OemEventAsync(P.OemControlEvent controlEvent, CancellationToken token)
    {
        if (IsActive || controlEvent.Edge == P.OemControlEdge.Released)
        {
            OemEventReceived?.Invoke(controlEvent);
        }

        return ValueTask.CompletedTask;
    }

    void P.IHandheldObserver.Faulted(string scope, string message)
    {
        PublishLifecycle(DeviceCycleState.Faulted,
            new CapabilityReason(CapabilityReasonCode.TransportFaulted, $"{scope}: {message}"));
        _completion.TrySetResult(new DeviceRuntimeExit(DeviceRuntimeExitReason.BackgroundFault, $"{scope}: {message}"));
    }

    internal event Action<HandheldRuntimeState>? LifecycleStateReceived;

    internal event Action<(IReadOnlyList<P.PhysicalDeviceIdentity> Devices, P.HapticCapabilities? Output)>?
        PhysicalIdentitiesReceived;

    internal event Action<P.CanonicalControllerSample>? ControllerSampleReceived;
    internal event Action<IReadOnlyList<P.OemControlDescriptor>>? OemControlsReceived;
    internal event Action<P.OemControlEvent>? OemEventReceived;

    internal static Task<HandheldDeviceRuntime> CreateAsync(P.HandheldDefinition definition,
        P.DeviceIdentitySnapshot identity, CancellationToken token, string stateRoot)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetFullPath(stateRoot), definition.FamilyId);
        var device = HandheldDevice.Create(definition, identity, directory, Trace, new P.HandheldResources
        {
            IntelKxPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Intel", "KX", "KX.exe"),
            InpOutPath = Path.Combine(AppContext.BaseDirectory, "Resources", "InpOut", "inpoutx64.dll")
        });
        Log.Info($"Handheld selected: model={definition.Id}, family={definition.FamilyId}, source={SourceVersion}, "
                 + $"bios={identity.BiosVersion ?? "unknown"}, hardwareVerified={definition.HardwareVerified}.");
        return Task.FromResult(new HandheldDeviceRuntime(device, directory));
    }

    internal Task<HandheldRuntimeState> StartAsync(bool controllerManagementEnabled, CancellationToken token)
    {
        return LifecycleAsync(async ct =>
        {
            PublishLifecycle(DeviceCycleState.Activating);
            var result = await Device.StartAsync(controllerManagementEnabled, ct).ConfigureAwait(false);
            return PublishStart(result);
        }, Deadline.After(TimeSpan.FromSeconds(15)), token);
    }

    internal Task<HandheldRuntimeState> SuspendAsync(Deadline deadline, CancellationToken token)
    {
        return LifecycleAsync(async ct =>
        {
            PublishLifecycle(DeviceCycleState.Deactivating);
            await Device.SuspendAsync(Budget(deadline), ct).ConfigureAwait(false);
            return PublishLifecycle(DeviceCycleState.Suspended);
        }, deadline, token);
    }

    internal Task<HandheldRuntimeState> ResumeAsync(Deadline deadline, CancellationToken token)
    {
        return LifecycleAsync(async ct =>
        {
            PublishLifecycle(DeviceCycleState.Activating);
            return PublishStart(await Device.ResumeAsync(Budget(deadline), ct).ConfigureAwait(false));
        }, deadline, token);
    }

    internal Task<HandheldRuntimeState> StopAsync(Deadline deadline, CancellationToken token)
    {
        lock (_workGate)
        {
            return _stop ??= LifecycleAsync(async ct =>
            {
                PublishLifecycle(DeviceCycleState.Deactivating);
                var result = await Device.StopAsync(Budget(deadline), ct).ConfigureAwait(false);
                var reason = result.Reason is null ? null : HandheldUiProjection.Reason(result.Reason);
                if (result.Status != P.DeviceStopStatus.Clean && reason is null)
                {
                    reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        $"Native restoration completed with {result.Status} evidence.");
                }

                return PublishLifecycle(DeviceCycleState.Disabled, reason);
            }, deadline, token);
        }
    }

    internal async Task ReleaseControllerAsync(Deadline deadline, CancellationToken token)
    {
        await AwaitWorkAsync(Device.ReleaseControllerAsync(Budget(deadline), token).AsTask(), deadline, token)
            .ConfigureAwait(false);
    }

    internal async Task SetControllerManagementAsync(bool enabled, Deadline deadline, CancellationToken token)
    {
        await AwaitWorkAsync(Device.SetControllerManagementAsync(enabled, Budget(deadline), token).AsTask(), deadline,
            token).ConfigureAwait(false);
    }

    internal Task ApplyHapticOutputAsync(P.HapticOutputFrame output, CancellationToken token)
    {
        return IsActive ? Device.ApplyHapticsAsync(output, token).AsTask() : Task.CompletedTask;
    }

    private async Task<CapabilityCommandResult> DispatchAsync(CapabilityCommand command, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return Rejected(command, "Cancelled before native dispatch.");
        }

        if (_hasSoftwareFanCurve && command.CapabilityId == CapabilityIds.FanCurve
                                 && command.InstanceId is null && command.RequestedValue is
                                     { Kind: CapabilityValueKind.Curve } curve)
        {
            await ApplySoftwareFanCurveAsync!(curve.CurveValue, token).ConfigureAwait(false);
            _softwareFanCurve = curve.CurveValue.ToArray();
            PublishSoftwareFanCurve();
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = CommandOutcome.Applied, CompletedAt = DateTimeOffset.UtcNow
            };
        }

        if (UsesSoftwareChargeLimit && command.CapabilityId == CapabilityIds.ChargeLimit
                                    && command.InstanceId is null &&
                                    command.RequestedValue?.IntegerValue is { } percent)
        {
            await ApplySoftwareChargeLimitAsync!(percent, token).ConfigureAwait(false);
            _softwareChargeLimit = percent;
            PublishSoftwareChargeLimit();
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = CommandOutcome.Applied, CompletedAt = DateTimeOffset.UtcNow
            };
        }

        var control = Device.Controls.Controls.SingleOrDefault(item =>
            item.Descriptor.CapabilityId == command.CapabilityId
            && item.InstanceId == command.InstanceId);
        if (control is null)
        {
            return Rejected(command, "The native control is no longer declared.");
        }

        var value = command.RequestedValue;
        var deadline = Budget(command.Deadline);
        P.DeviceOperationResult? operation = null;
        if (value is not null)
        {
            var instance = control.InstanceId;
            operation = control.Role switch
            {
                P.CapabilityRole.PowerSustainedLimit => await WritePowerAsync(command, control, token)
                    .ConfigureAwait(false),
                P.CapabilityRole.PowerSlowLimit or P.CapabilityRole.PowerFastLimit or P.CapabilityRole.PowerPeakLimit
                    when command.PairedPowerLimitWatts is not null => await WritePowerAsync(command, control, token)
                        .ConfigureAwait(false),
                P.CapabilityRole.PowerSlowLimit or P.CapabilityRole.PowerFastLimit or P.CapabilityRole.PowerPeakLimit =>
                    await Device.SetControlAsync(control.Role, HandheldUiProjection.NativeValue(value),
                        control.InstanceId, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.ScenarioMode => await Device.SetScenarioModeAsync(value.ChoiceValue!, deadline, token)
                    .ConfigureAwait(false),
                P.CapabilityRole.FanMode => await Device.SetFanModeAsync(value.ChoiceValue!, instance, deadline, token)
                    .ConfigureAwait(false),
                P.CapabilityRole.FanDuty => await Device
                    .SetFanDutyAsync(value.IntegerValue!.Value, instance, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.FanTargetRpm => await Device
                    .SetFanTargetRpmAsync(value.IntegerValue!.Value, instance, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.FanCurve => await Device
                    .SetFanCurveAsync(HandheldUiProjection.NativeValue(value).CurveValue, instance, deadline, token)
                    .ConfigureAwait(false),
                P.CapabilityRole.ChargeLimit => await Device
                    .SetChargeLimitAsync(value.IntegerValue!.Value, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.ChargeProtectionMode when value.BooleanValue is { } enabled => await Device
                    .SetChargeProtectionAsync(enabled, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.ChargeProtectionMode => await Device
                    .SetChargeProtectionModeAsync(value.ChoiceValue!, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.ChargeBypass when value.BooleanValue is { } enabled => await Device
                    .SetChargeBypassAsync(enabled, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.ChargeBypass => await Device
                    .SetChargeBypassModeAsync(value.ChoiceValue!, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.LightingPower => await Device
                    .SetLightingPowerAsync(value.BooleanValue!.Value, instance, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.LightingBrightness => await Device
                    .SetLightingBrightnessAsync(value.IntegerValue!.Value, instance, deadline, token)
                    .ConfigureAwait(false),
                P.CapabilityRole.LightingZoneColor => await Device
                    .SetLightingColorAsync(value.ColorValue!.Value, instance, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.LightingEffect => await Device
                    .SetLightingEffectAsync(value.ChoiceValue!, instance, deadline, token).ConfigureAwait(false),
                P.CapabilityRole.LightingEffectSpeed => await Device
                    .SetLightingSpeedAsync(value.IntegerValue!.Value, instance, deadline, token).ConfigureAwait(false),
                _ => null
            };
        }

        if (operation is not null)
        {
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId, Outcome = HandheldUiProjection.Outcome(operation.Outcome),
                Reason = operation.Reason is null ? null : HandheldUiProjection.Reason(operation.Reason),
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        var native = new P.CapabilityCommand
        {
            CommandId = command.CommandId, CapabilityId = command.CapabilityId, InstanceId = command.InstanceId,
            RequestedValue = value is null ? null : HandheldUiProjection.NativeValue(value),
            PairedPowerLimitWatts = command.PairedPowerLimitWatts, Deadline = deadline
        };
        return HandheldUiProjection.Result(await Device.ExecuteAsync(native, token).ConfigureAwait(false));
    }

    private ValueTask<P.DeviceOperationResult> WritePowerAsync(CapabilityCommand command, P.DeviceControl control,
        CancellationToken token)
    {
        var watts = command.RequestedValue!.IntegerValue!.Value;
        var peer = command.PairedPowerLimitWatts;
        var deadline = Budget(command.Deadline);
        if (peer == watts)
        {
            return Device.SetTdpAsync(watts, deadline, token);
        }

        var sustained = control.Role == P.CapabilityRole.PowerSustainedLimit ? watts : peer!.Value;
        var peerControl = Device.Controls.Controls.FirstOrDefault(item =>
                              item.Descriptor.CapabilityId == control.Descriptor.PairedPowerLimitId)
                          ?? Device.Controls.Controls.FirstOrDefault(item =>
                              item.Descriptor.PairedPowerLimitId == control.Descriptor.CapabilityId);
        var boostRole = control.Role == P.CapabilityRole.PowerSustainedLimit ? peerControl?.Role : control.Role;
        var boost = control.Role == P.CapabilityRole.PowerSustainedLimit ? peer : watts;
        return Device.SetPowerLimitsAsync(new P.PowerLimits(sustained,
            boostRole == P.CapabilityRole.PowerSlowLimit ? boost : null,
            boostRole == P.CapabilityRole.PowerFastLimit ? boost : null,
            boostRole == P.CapabilityRole.PowerPeakLimit ? boost : null), deadline, token);
    }

    internal async ValueTask DisposeAsync(Deadline deadline)
    {
        lock (_workGate)
        {
            _disposed = true;
            _dispose ??= DisposeHandlesAsync();
            LateCleanup = _dispose;
        }

        await AwaitWorkAsync(_dispose, deadline, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DisposeHandlesAsync()
    {
        Task[] pending;
        lock (_workGate)
        {
            pending = _work.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn($"Handheld work ended during disposal: {exception.Message}");
        }

        await Device.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _completion.TrySetResult(new DeviceRuntimeExit(DeviceRuntimeExitReason.Intentional,
            "Native handles released."));
    }

    private async Task<HandheldRuntimeState> LifecycleAsync(Func<CancellationToken, Task<HandheldRuntimeState>> action,
        Deadline deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var work = action(token);
        Track(work);
        await AwaitWorkAsync(work, deadline, token).ConfigureAwait(false);
        return await work.ConfigureAwait(false);
    }

    private async Task AwaitWorkAsync(Task work, Deadline deadline, CancellationToken token)
    {
        Track(work);
        using var bounded = deadline.CreateCancellationSource(token);
        try
        {
            await work.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LateCleanup = work;
            Log.Observe(work, "Late native handheld operation", true);
            throw;
        }
    }

    private void Track(Task task)
    {
        lock (_workGate)
        {
            if (!_work.Add(task))
            {
                return;
            }
        }

        _ = RemoveFinishedAsync(task);
    }

    private async Task RemoveFinishedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn($"Native handheld operation ended: {exception.Message}");
        }
        finally
        {
            lock (_workGate)
            {
                _work.Remove(task);
            }
        }
    }

    private HandheldRuntimeState PublishStart(P.DeviceStartResult result)
    {
        return PublishLifecycle(result.State switch
        {
            P.DeviceOperationalState.Active => DeviceCycleState.Active,
            P.DeviceOperationalState.Degraded => DeviceCycleState.Degraded,
            _ => DeviceCycleState.Passive
        }, result.Reason is null ? null : HandheldUiProjection.Reason(result.Reason));
    }

    private HandheldRuntimeState PublishLifecycle(DeviceCycleState state, CapabilityReason? reason = null)
    {
        _state = state;
        var snapshot = new HandheldRuntimeState(state, Device.Definition.Id, reason);
        LifecycleStateReceived?.Invoke(snapshot);
        return snapshot;
    }

    private static P.Deadline Budget(Deadline deadline)
    {
        return P.Deadline.After(deadline.Remaining);
    }

    private static CapabilityCommandResult Rejected(CapabilityCommand command, string detail)
    {
        return Result(command, CommandOutcome.Rejected, detail);
    }

    private static CapabilityCommandResult Result(CapabilityCommand command, CommandOutcome outcome, string detail)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId, Outcome = outcome,
            Reason = new CapabilityReason(CapabilityReasonCode.Quiescing, detail), CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static void Trace(P.DeviceLogEntry entry)
    {
        var detail = $"Handheld {entry.Scope}: {entry.Message}";
        if (entry.Level == P.DeviceLogLevel.Error)
        {
            Log.Error(detail);
        }
        else if (entry.Level == P.DeviceLogLevel.Warn)
        {
            Log.Warn(detail);
        }
        else if (entry.Level == P.DeviceLogLevel.Debug)
        {
            Log.Debug(detail);
        }
        else
        {
            Log.Info(detail);
        }
    }

    internal void ReportSoftwareFanStatus(string? reason)
    {
        _softwareFanReason = reason;
        PublishSoftwareFanCurve();
    }

    private void PublishSoftwareFanCurve()
    {
        if (!_hasSoftwareFanCurve)
        {
            return;
        }

        var available = (IsActive || AcceptsPublications)
                        && Device.Controls.Controls.Any(item =>
                            item.Role == P.CapabilityRole.FanDuty && item.State?.Available == true);
        CapabilityStateReceived?.Invoke(new CapabilityStateDelta(Interlocked.Increment(ref _stateSequence),
            new CapabilityState
            {
                CapabilityId = CapabilityIds.FanCurve, Available = available,
                Reason = _softwareFanReason is null
                    ? null
                    : new CapabilityReason(CapabilityReasonCode.ObservationExpired,
                        _softwareFanReason),
                ObservedValue = _softwareFanCurve.Count == 0
                    ? null
                    : new CapabilityValue { Kind = CapabilityValueKind.Curve, CurveValue = _softwareFanCurve },
                Quality = _softwareFanCurve.Count == 0 ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = _softwareFanCurve.Count == 0 ? null : DateTimeOffset.UtcNow
            }));
    }

    private void PublishSoftwareChargeLimit()
    {
        if (!UsesSoftwareChargeLimit)
        {
            return;
        }

        var available = (IsActive || AcceptsPublications) && Device.Controls.Controls.Any(item =>
            item.Role == P.CapabilityRole.ChargeBypass && item.State?.Available == true);
        CapabilityStateReceived?.Invoke(new CapabilityStateDelta(Interlocked.Increment(ref _stateSequence),
            new CapabilityState
            {
                CapabilityId = CapabilityIds.ChargeLimit, Available = available,
                ObservedValue = _softwareChargeLimit is { } limit ? CapabilityValue.Integer(limit) : null,
                Quality = _softwareChargeLimit is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = _softwareChargeLimit is null ? null : DateTimeOffset.UtcNow
            }));
    }
}

internal sealed record HandheldRuntimeState(
    DeviceCycleState State,
    string? DeviceDefinitionId,
    CapabilityReason? Reason);

internal enum HandheldStopReason
{
    WsgmExiting,
    IntegrationDisabled,
    RuntimeFault,
    StartCanceled,
    StartFailed,
    SessionEnding,
    Updating,
    Uninstalling
}

internal enum DeviceRuntimeExitReason
{
    Intentional,
    BackgroundFault
}

internal sealed record DeviceRuntimeExit(DeviceRuntimeExitReason Reason, string Detail);

internal sealed record DeviceCommandDispatch(
    CapabilityCommandResult Immediate,
    Task<CapabilityCommandResult>? LateCompletion = null);
