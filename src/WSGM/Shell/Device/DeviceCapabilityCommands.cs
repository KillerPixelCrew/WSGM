using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The one route device capability commands and desired-value restores take to the handheld.</summary>
/// <remarks>
///     Every write from a surface, a controller or a restore passes through here: power roles wait for
///     the power policy's lane, user writes are recorded in the layer in force, and desired values are
///     reconciled per capability under the desired-state gate. A manual power change pauses AutoTDP only
///     on this route.
/// </remarks>
internal sealed class DeviceCapabilityCommands
{
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly DeviceChargeLimitController _chargeLimits;
    private readonly Func<bool> _closed;
    private readonly Func<string?> _deviceIdentityKey;
    private readonly DeviceFanCurves _fanCurves;
    private readonly Func<bool> _identified;
    private readonly CancellationToken _lifetime;
    private readonly DeviceLightingProfiles _lighting;
    private readonly Action<bool> _manualVariableRefresh;
    private readonly DevicePowerPolicy _power;
    private readonly ProfileService _profiles;

    /// <summary>Serializes desired-value passes, lighting profile selection and processor resets.</summary>
    private readonly SemaphoreSlim _reconcileGate;

    private readonly Func<Task> _reconcileChargeLimit;
    private readonly Func<HandheldDeviceRuntime?> _runtime;
    private int _userCommands;

    /// <summary>Composes the command route for one device coordinator.</summary>
    /// <param name="capabilities">The device capability router commands are dispatched through.</param>
    /// <param name="profiles">The profile owner user values are saved to.</param>
    /// <param name="power">The power policy whose lane power writes run in.</param>
    /// <param name="fanCurves">The software fan curve a fan mode or duty write reconfigures.</param>
    /// <param name="lighting">The lighting owner a zone color write and restore consult.</param>
    /// <param name="chargeLimits">The software charge ceiling a direct bypass write suspends.</param>
    /// <param name="reconcileChargeLimit">Reconciles the software charge ceiling with its desired value.</param>
    /// <param name="reconcileGate">The desired-state gate shared with lighting profile selection.</param>
    /// <param name="runtime">Reads the active handheld runtime, or null without a cycle.</param>
    /// <param name="identified">Whether the machine identity was collected.</param>
    /// <param name="deviceIdentityKey">Reads the key device values are stored under, or null before identification.</param>
    /// <param name="manualVariableRefresh">Persists an explicitly chosen variable-refresh value to the active profile.</param>
    /// <param name="closed">Whether the coordinator closed admission.</param>
    /// <param name="lifetime">The coordinator's lifetime token, cancelled when admission closes.</param>
    internal DeviceCapabilityCommands(
        DeviceCapabilityRouter capabilities,
        ProfileService profiles,
        DevicePowerPolicy power,
        DeviceFanCurves fanCurves,
        DeviceLightingProfiles lighting,
        DeviceChargeLimitController chargeLimits,
        Func<Task> reconcileChargeLimit,
        SemaphoreSlim reconcileGate,
        Func<HandheldDeviceRuntime?> runtime,
        Func<bool> identified,
        Func<string?> deviceIdentityKey,
        Action<bool> manualVariableRefresh,
        Func<bool> closed,
        CancellationToken lifetime)
    {
        _capabilities = capabilities;
        _profiles = profiles;
        _power = power;
        _fanCurves = fanCurves;
        _lighting = lighting;
        _chargeLimits = chargeLimits;
        _reconcileChargeLimit = reconcileChargeLimit;
        _reconcileGate = reconcileGate;
        _runtime = runtime;
        _identified = identified;
        _deviceIdentityKey = deviceIdentityKey;
        _manualVariableRefresh = manualVariableRefresh;
        _closed = closed;
        _lifetime = lifetime;
    }

    /// <summary>Whether a user command is running, which defers automatic lighting restores.</summary>
    internal bool UserCommandsInFlight => Volatile.Read(ref _userCommands) != 0;

    /// <summary>The refusal every command gets once the device session stops admitting work.</summary>
    /// <returns>A rejected result naming the stopping session.</returns>
    internal static CapabilityCommandResult ClosedAdmission()
    {
        return new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                "The device session is stopping."),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Routes one semantic capability command through current validation and serialization.</summary>
    /// <param name="capabilityId">The capability being commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="origin">Who asked for it, which decides whether AutoTDP steps aside.</param>
    /// <param name="applyPowerPair">Whether both limits of the declared sustained/boost pair move to this target.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result reported by the handheld.</returns>
    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CapabilityCommandOrigin origin = CapabilityCommandOrigin.User,
        bool applyPowerPair = false,
        CancellationToken cancellationToken = default)
    {
        if (_closed() && origin is not CapabilityCommandOrigin.AutoTdp)
        {
            return ClosedAdmission();
        }

        using var admission = origin is CapabilityCommandOrigin.AutoTdp
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        cancellationToken = admission?.Token ?? cancellationToken;
        var role = FindDescriptor(capabilityId, instanceId)?.Role;
        var power = role is
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit or CapabilityRole.PowerFastLimit
                or CapabilityRole.PowerPeakLimit or CapabilityRole.ScenarioMode;
        if (origin == CapabilityCommandOrigin.User && role == CapabilityRole.PowerSustainedLimit)
        {
            applyPowerPair |= _profiles.Current.Layers.ManualTdp()?.Unified == true;
        }

        if (power)
        {
            await _power.Lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ExecuteCoreAsync(capabilityId, instanceId, value, timeout, origin,
                    applyPowerPair, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (power)
            {
                _power.Lane.Release();
            }
        }
    }

    /// <summary>Routes one command whose power lane, when it needs one, the caller already holds.</summary>
    /// <param name="capabilityId">The capability being commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="origin">Who asked for it, which decides persistence and whether AutoTDP steps aside.</param>
    /// <param name="applyPowerPair">Whether both limits of the declared sustained/boost pair move to this target.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result reported by the handheld.</returns>
    internal async Task<CapabilityCommandResult> ExecuteCoreAsync(
        string capabilityId, string? instanceId, CapabilityValue? value, TimeSpan timeout,
        CapabilityCommandOrigin origin, bool applyPowerPair,
        CancellationToken cancellationToken)
    {
        if (_closed() && origin is not CapabilityCommandOrigin.AutoTdp)
        {
            return ClosedAdmission();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = origin is CapabilityCommandOrigin.User;
        if (user)
        {
            Interlocked.Increment(ref _userCommands);
        }

        var processorResetLane = false;
        try
        {
            if (user && capabilityId is CapabilityIds.ProcessorReset or CapabilityIds.ProcessorGpuClockAutomatic
                     && value is null
                     && _deviceIdentityKey() is { } processorDevice && FindCapability(capabilityId, instanceId) is not null)
            {
                // Join any in-flight profile pass before clearing intent and restoring hardware.
                // Otherwise an already-admitted old clock could be written after the reset.
                await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                processorResetLane = true;
                // Remove every saved layer first so a wake or a dormant game cannot reimpose tuning.
                // A persistence failure must leave hardware untouched.
                await _profiles.ClearProcessorTuningAsync(processorDevice, cancellationToken,
                        capabilityId == CapabilityIds.ProcessorReset ? null : CapabilityIds.ProcessorGpuClock)
                    .ConfigureAwait(false);
                _lighting.UpdateDesiredContext();
            }

            if (user && FindDescriptor(capabilityId, instanceId) is { } manualDescriptor)
            {
                if (manualDescriptor.Role == CapabilityRole.ChargeBypass
                    && _runtime() is { UsesSoftwareChargeLimit: true } runtime)
                {
                    await _chargeLimits.SuspendAsync().ConfigureAwait(false);
                    runtime.ReportSoftwareChargeSuspended();
                }

                if (manualDescriptor.Role is CapabilityRole.FanDuty or CapabilityRole.FanMode)
                {
                    // The chosen mode decides who drives this fan: Software starts the curve on it, and
                    // anything else, a manual duty included, hands it back before the write.
                    await _fanCurves.ConfigureAsync(_fanCurves.SavedSoftwareCurve(), explicitSelection: true,
                        (instanceId ?? string.Empty,
                            manualDescriptor.Role == CapabilityRole.FanMode
                            && value?.ChoiceValue == FanControlModes.Software)).ConfigureAwait(false);
                }
                else if (manualDescriptor.Role == CapabilityRole.FanCurve && instanceId is not null)
                {
                    await _fanCurves.ConfigureAsync(null, explicitSelection: true, stopAll: true)
                        .ConfigureAwait(false);
                }
            }

            if (user && value is not null && _identified()
                && FindCapability(capabilityId, instanceId) is { } view)
            {
                var handled = await CapabilityUserWrites.HandleUserWriteAsync(_profiles,
                    _deviceIdentityKey()!, view, value, async () =>
                    {
                        var written = await _capabilities.ExecuteAsync(capabilityId, instanceId, value, timeout,
                            applyPowerPair,
                            cancellationToken).ConfigureAwait(false);
                        _power.NotifyManualPowerChange(capabilityId, instanceId, value, written);
                        if (written.Outcome.IsApplied() && view.Descriptor.Role == CapabilityRole.LightingZoneColor)
                        {
                            await _lighting.ManualOverrideAsync(cancellationToken).ConfigureAwait(false);
                        }

                        return written;
                    }, _manualVariableRefresh, cancellationToken).ConfigureAwait(false);
                _lighting.UpdateDesiredContext();
                return handled;
            }

            var result = await _capabilities.ExecuteAsync(
                capabilityId,
                instanceId,
                value,
                timeout,
                applyPowerPair, cancellationToken).ConfigureAwait(false);
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (origin)
            {
                case CapabilityCommandOrigin.User:
                    _power.NotifyManualPowerChange(capabilityId, instanceId, value, result);
                    _power.NotifyManualVariableRefreshChange(capabilityId, instanceId, value, result);
                    await PersistUserValueAsync(
                        capabilityId,
                        instanceId,
                        value,
                        result,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case CapabilityCommandOrigin.DesiredStateRestore
                    when result.Outcome.IsApplied()
                         && FindDescriptor(capabilityId, instanceId)?.Role is CapabilityRole.PowerSustainedLimit
                         && value?.IntegerValue is { } watts:
                    _power.AutoTdp.NoteManualChange(watts);
                    break;
            }

            return result;
        }
        finally
        {
            if (processorResetLane)
            {
                _reconcileGate.Release();
            }

            if (user)
            {
                Interlocked.Decrement(ref _userCommands);
            }
        }
    }

    /// <summary>Writes every persistent desired value the hardware does not already hold.</summary>
    /// <param name="reason">What asked for the reconciliation, for the log.</param>
    /// <param name="cancellationToken">Cancels the remaining commands.</param>
    /// <param name="lightingOnly">Limits readiness-triggered restoration to lighting.</param>
    /// <returns>A task completing once every affected capability has been attempted.</returns>
    /// <remarks>
    ///     Per-capability and independent: one refusal must not stop the rest, because a profile that
    ///     applied its fan curve but not its power limit is still better than one that applied nothing.
    ///     A value the device already reports is skipped, so reselecting the active profile is free.
    /// </remarks>
    internal async Task ReconcileDesiredValuesAsync(
        string reason, CancellationToken cancellationToken, bool lightingOnly = false)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed())
            {
                return;
            }

            await _lighting.ReconcileProfileAsync(_profiles.Current, cancellationToken).ConfigureAwait(false);
            await CapabilityDesiredReconciler.RunAsync(
                new CapabilityReconcilePass(
                    "Device",
                    _capabilities.Snapshot,
                    _capabilities.TryGetView,
                    (view, desired, token) => RestoreDesiredValueAsync(view, desired, reason, token))
                {
                    Priority = ReconciliationPriority,
                    Include = view => !_lighting.OwnsProfile(view)
                                      && (!lightingOnly || DeviceLightingRestore.IsLighting(view.Descriptor.Role))
                                      && !(view.Descriptor.Role is CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm
                                               or CapabilityRole.FanCurve
                                           && ManualFanVisibility.Hides(_capabilities.Snapshot(), view))
                                      && !(view.Descriptor.Role is CapabilityRole.PowerSlowLimit
                                               or CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit
                                           && (_power.ManualTdpUnified || _power.PowerAssignments.HasCurrentAssignment)),
                    Abandon = () => lightingOnly && UserCommandsInFlight
                },
                reason,
                cancellationToken).ConfigureAwait(false);
            if (!lightingOnly)
            {
                await _fanCurves.ApplyAuthoredProfilesAsync(_profiles.Current, cancellationToken)
                    .ConfigureAwait(false);
                await _reconcileChargeLimit().ConfigureAwait(false);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>Records a value the user just set as the desired state of the layer in force.</summary>
    /// <param name="capabilityId">The capability that was commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="result">What the handheld reported.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the value is stored, or immediately when it is not.</returns>
    /// <remarks>
    ///     Without this the Device surface commanded hardware and remembered nothing: every row went
    ///     back to whatever the firmware held on the next cycle or the next boot. The descriptor's profile
    ///     scope and the profile store decide the layer (<see cref="CapabilityUserWrites" />).
    ///     <para>
    ///         Applied after the device took the value, not before: recording a preference the hardware
    ///         refused would restore a value on the next launch that the device never accepted. An
    ///         unverified write still counts, because the desired state is what the user asked for and the
    ///         handheld reports that it wrote it.
    ///     </para>
    /// </remarks>
    private async Task PersistUserValueAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result,
        CancellationToken cancellationToken)
    {
        if (value is null
            || !result.Outcome.IsApplied())
        {
            return;
        }

        var view = FindCapability(capabilityId, instanceId);
        if (view is null
            || !view.Descriptor.SupportsWrite
            || CapabilityUserWrites.PerformanceProfileOwnsRole(view.Descriptor.Role))
        {
            return;
        }

        if (!_identified())
        {
            Log.Warn(
                $"Device value for {capabilityId}{Instance(instanceId)} was applied but not saved: "
                + "this machine has no resolved device identity to store it under.");
            return;
        }

        // Restores never reach persistence. A user control landing back on the desired value
        // also needs no configuration write.
        if (view.Descriptor.Role == CapabilityRole.LightingZoneColor)
        {
            await _lighting.ManualOverrideAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await CapabilityUserWrites.PersistAsync(_profiles, _deviceIdentityKey()!, view,
                value, cancellationToken).ConfigureAwait(false))
        {
            _lighting.UpdateDesiredContext();
        }
    }

    /// <summary>Writes one admitted desired value, with the lighting attempt budget around it.</summary>
    /// <returns>The result, or null when a lighting zone has no attempt left.</returns>
    private async Task<CapabilityCommandResult?> RestoreDesiredValueAsync(
        DeviceCapabilityView view,
        CapabilityValue desired,
        string reason,
        CancellationToken cancellationToken)
    {
        var lighting = DeviceLightingRestore.IsLighting(view.Descriptor.Role);
        var attempt = lighting ? _lighting.Restore.TryBegin(view) : 0;
        if (lighting && attempt == 0)
        {
            return null;
        }

        var outcome = CommandOutcome.Indeterminate;
        try
        {
            // The pass is cancelled between writes, never inside one: a write cancelled in flight
            // ends Indeterminate, and a device without readback can never settle it. A profile
            // change 700 ms after a game started left the Ally's scenario that way for the rest of
            // the session (2026-09-29). The command's own deadline still bounds the wait.
            var result = await ExecuteAsync(
                view.Descriptor.CapabilityId,
                view.Descriptor.InstanceId,
                desired,
                TimeSpan.FromSeconds(5),
                CapabilityCommandOrigin.DesiredStateRestore,
                cancellationToken: _lifetime).ConfigureAwait(false);
            outcome = result.Outcome;
            return result;
        }
        finally
        {
            if (lighting)
            {
                _lighting.Restore.Complete(view, outcome);
                Log.Change(
                    $"device-restore/{view.Descriptor.CapabilityId}{Instance(view.Descriptor.InstanceId)}",
                    $"Device restore {view.Descriptor.CapabilityId}{Instance(view.Descriptor.InstanceId)} "
                    + $"from {view.Projection.DesiredSource} ({reason}): outcome={outcome}, "
                    + $"attempt {attempt} of {DeviceLightingRestore.MaxAttempts}.",
                    outcome.IsApplied() ? LogLevel.Info : LogLevel.Warn);
            }
        }
    }

    /// <summary>Orders coupled power writes so their transient pair remains valid.</summary>
    /// <param name="view">Capability and its observed and desired values.</param>
    /// <returns>Lower values are written first.</returns>
    private static int ReconciliationPriority(DeviceCapabilityView view)
    {
        var observed = view.Projection.State.ObservedValue?.IntegerValue;
        var desired = view.Projection.DesiredValue?.IntegerValue;
        return view.Descriptor.Role switch
        {
            // The power-limit path first, so the saved limits are written through the chosen path.
            CapabilityRole.PowerBackend => -1,
            // Lower PL1 before lowering PL2, otherwise the new PL2 can fall below the old PL1.
            CapabilityRole.PowerSustainedLimit when desired < observed => 0,
            // Raise PL2 before raising PL1, otherwise the new PL1 can exceed the old PL2.
            CapabilityRole.PowerSlowLimit or CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit
                when desired > observed => 0,
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit or CapabilityRole.PowerFastLimit
                or CapabilityRole.PowerPeakLimit => 1,
            _ => 2
        };
    }

    private static string Instance(string? instanceId)
    {
        return instanceId is { Length: > 0 } ? $"/{instanceId}" : string.Empty;
    }

    /// <summary>The published view of one capability instance, or null when none is published.</summary>
    private DeviceCapabilityView? FindCapability(string capabilityId, string? instanceId)
    {
        return _capabilities.TryGetView(new DeviceCapabilityKey(capabilityId, instanceId));
    }

    private CapabilityDescriptor? FindDescriptor(string capabilityId, string? instanceId)
    {
        return FindCapability(capabilityId, instanceId)?.Descriptor;
    }
}
