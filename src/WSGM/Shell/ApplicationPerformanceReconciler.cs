using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     Carries the running application's per-game power limit, variable refresh preference and
///     processor boost mode to the device and to Windows, and saves values set by hand to the
///     profile layer in force.
/// </summary>
/// <remarks>
///     The session decides when this runs; this remembers what it imposed, so a value one
///     application set is undone when the next application does not ask for it.
/// </remarks>
/// <param name="profiles">The profile owner the values are read from and saved to.</param>
/// <param name="readCoordinator">Reads the device coordinator, or null without device integration.</param>
/// <param name="readAutoTdp">Reads AutoTDP, or null when it is not running.</param>
/// <param name="cpuBoost">Windows' processor boost mode, or null when this session must not write it.</param>
internal sealed class ApplicationPerformanceReconciler(
    ProfileService profiles,
    Func<DeviceCoordinator?> readCoordinator,
    Func<AutoTdpService?> readAutoTdp,
    CpuBoost? cpuBoost = null)
{
    private readonly Lock _cpuBoostGate = new();
    private CpuBoostMode? _cpuBoostBaseline;
    private bool _cpuBoostImposed;
    private volatile CpuBoostStatus? _cpuBoostStatus;
    private bool _cpuBoostUnsupportedLogged;
    private string _lastReconciledApplicationId = "(uninitialised)";
    private string _lastReconciledCpuBoostKey = "(uninitialised)";
    private bool _profilePowerImposed;
    private bool _profilePowerPaired;
    private bool _profileVrrImposed;

    /// <summary>Whether this session can read and write the processor boost mode at all.</summary>
    internal bool CpuBoostAvailable => cpuBoost is not null;

    /// <summary>The last processor boost readback, or null before the first read.</summary>
    internal CpuBoostStatus? CpuBoostStatus => _cpuBoostStatus;

    /// <summary>Raised after the processor boost readback changes, from whichever thread read it.</summary>
    internal event Action? CpuBoostChanged;

    /// <summary>
    ///     Restores the power limit and variable-refresh state the incoming application prefers, and takes
    ///     back the ones the outgoing application imposed.
    /// </summary>
    /// <param name="snapshot">The profiles and the application they resolve for.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    ///     The fix for a power limit or refresh mode set in a game leaking onto the desktop after the game
    ///     closes. Each value comes from the game profile when it sets one and from Global otherwise. When neither
    ///     layer prefers a value, the outgoing application's is undone rather than left running — for
    ///     power, automatic control resumes if it is on and the limit is otherwise released to the device
    ///     ceiling; for variable refresh, it returns to off — but only when WSGM actually imposed the
    ///     current one, so a session that never used the feature is never touched. Both decisions are pure
    ///     and tested (<see cref="PerApplicationPowerPolicy" />, <see cref="PerApplicationVrrPolicy" />);
    ///     this only reads the layers and carries them out.
    /// </remarks>
    internal async Task ReconcileApplicationProfileAsync(
        ProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Keyed on what resolves, not on the snapshot generation: enriching the executable of the same
        // game, or saving an unrelated value, must not rewrite the limit. A value the user just set by
        // hand already matches the device and is skipped below.
        var layers = snapshot.Layers;
        var applicationId = snapshot.Active.ApplicationId;
        var manual = layers.ManualTdp();
        var vrrPreference = layers.Value(values => values.VariableRefreshRate).Value;
        // Windows policy, so it runs with or without a device plugin and keeps its own identity:
        // the device key below is deliberately not recorded while no capability exists.
        await ReconcileApplicationCpuBoostAsync(
            layers.Value(values => values.CpuBoost).Value,
            applicationId,
            cancellationToken).ConfigureAwait(false);

        var identityKey = $"{applicationId}|{manual}|{vrrPreference}";
        if (string.Equals(identityKey, _lastReconciledApplicationId, StringComparison.Ordinal))
        {
            return;
        }

        if (readCoordinator() is not { } coordinator)
        {
            return;
        }

        var power = FindPowerLimitCapability();
        var vrr = FindVariableRefreshCapability();
        if (power is null && vrr is null)
        {
            // No manageable device value: nothing this transition can do. The identity is not
            // recorded, so if a plugin publishes a capability later a transition still reconciles.
            return;
        }

        _lastReconciledApplicationId = identityKey;

        if (power is not null && !coordinator.PowerAssignments.HasCurrentAssignment)
        {
            await ReconcileApplicationPowerLimitAsync(
                power,
                coordinator,
                manual,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }

        if (vrr is not null)
        {
            await ReconcileApplicationVariableRefreshAsync(
                vrrPreference,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileApplicationPowerLimitAsync(
        DeviceCapabilityView power,
        DeviceCoordinator coordinator,
        ManualTdpProfile? manualProfile,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        var (effective, paired) = manualProfile is null
            ? (null, false)
            : manualProfile.Unified
                ? (manualProfile.UnifiedWatts, true)
                : (manualProfile.SustainedWatts, false);
        var ceiling = power.Descriptor.Maximum ?? 0;
        var autoTdpEnabled = coordinator.AutoTdpEnabled;
        var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            effective,
            _profilePowerImposed,
            autoTdpEnabled,
            ceiling);

        switch (decision.Action)
        {
            case PerAppPowerAction.Apply:
                var splitPair = manualProfile is { Unified: false, BoostWatts: not null };
                // A value the user just set by hand is already on the device; writing it again would
                // only pause AutoTDP a second time.
                bool applied;
                if (splitPair)
                {
                    applied = await coordinator.RestoreSplitPowerAsync(power, decision.Watts,
                        manualProfile!.BoostWatts!.Value, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    applied = power.Projection.State.ObservedValue?.IntegerValue == decision.Watts
                              || await ApplyProfilePowerLimitAsync(power, decision.Watts, cancellationToken, paired)
                                  .ConfigureAwait(false);
                }

                if (applied)
                {
                    // An explicit limit overrides automatic control exactly as moving the slider
                    // does; pausing while it is applied keeps AutoTDP from writing over it next tick.
                    if (autoTdpEnabled)
                    {
                        readAutoTdp()?.NoteManualChange(decision.Watts);
                    }

                    _profilePowerImposed = true;
                    _profilePowerPaired = paired || splitPair;
                    Log.Info(
                        $"Per-application power limit applied: {decision.Watts} W for "
                        + $"{applicationId ?? "the global profile"}.");
                }

                break;

            case PerAppPowerAction.ResumeAutomatic:
                readAutoTdp()?.ResumeAutomaticControl();
                _profilePowerImposed = false;
                _profilePowerPaired = false;
                Log.Info(
                    "Per-application power limit released; automatic control resumes for "
                    + $"{applicationId ?? "the global profile"}.");
                break;

            case PerAppPowerAction.ReleaseToCeiling:
                if (ceiling > 0
                    && await ApplyProfilePowerLimitAsync(power, ceiling, cancellationToken, _profilePowerPaired)
                        .ConfigureAwait(false))
                {
                    _profilePowerImposed = false;
                    _profilePowerPaired = false;
                    Log.Info(
                        $"Per-application power limit released to the device ceiling {ceiling} W for "
                        + $"{applicationId ?? "the global profile"}.");
                }

                break;

            case PerAppPowerAction.Leave:
                break;

            default:
                Log.Warn($"Per-application power decision {decision.Action} is unknown; the limit is left as is.");
                break;
        }
    }

    private async Task ReconcileApplicationVariableRefreshAsync(
        bool? effective,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        var decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effective,
            _profileVrrImposed);
        if (decision.Action is not PerAppVrrAction.Apply)
        {
            return;
        }

        if (await ApplyVariableRefreshRateAsync(
                decision.Enabled,
                CapabilityCommandOrigin.ProfileRestore,
                cancellationToken).ConfigureAwait(false))
        {
            _profileVrrImposed = effective is not null;
            Log.Info(
                $"Per-application variable refresh {(decision.Enabled ? "enabled" : "disabled")} for "
                + $"{applicationId ?? "the global profile"}.");
        }
    }

    private async Task ReconcileApplicationCpuBoostAsync(
        CpuBoostMode? effective,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        if (cpuBoost is null)
        {
            return;
        }

        var key = $"{applicationId}|{effective}";
        if (string.Equals(key, _lastReconciledCpuBoostKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastReconciledCpuBoostKey = key;
        var decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(
            effective,
            _cpuBoostImposed,
            _cpuBoostBaseline);
        if (decision.Action is not PerAppCpuBoostAction.Apply)
        {
            // Nothing preferred and nothing to take back. The imposed flag still clears, so a
            // mode that was never WSGM's to restore is not restored on some later transition.
            _cpuBoostImposed = false;
            return;
        }

        if (await ApplyCpuBoostAsync(decision.Mode, cancellationToken).ConfigureAwait(false))
        {
            _cpuBoostImposed = effective is not null;
            Log.Info(
                $"Per-application processor boost {CpuBoost.NameFor(decision.Mode)} for "
                + $"{applicationId ?? "the global profile"}.");
        }
    }

    /// <summary>Reads the processor boost mode from Windows and publishes it to both surfaces.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The readback, or null when this session has no processor boost.</returns>
    internal async Task<CpuBoostStatus?> RefreshCpuBoostAsync(CancellationToken cancellationToken = default)
    {
        if (cpuBoost is null)
        {
            return null;
        }

        var status = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cpuBoost.Read();
        }, cancellationToken).ConfigureAwait(false);
        PublishCpuBoost(status);
        return status;
    }

    /// <summary>Applies a processor boost mode the user chose and saves it to the layer in force.</summary>
    /// <param name="mode">The mode the user chose.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether Windows confirmed the mode.</returns>
    /// <remarks>
    ///     The user-facing counterpart to the transition's own write. Saving happens here rather than
    ///     in a device hook, because Windows has no manual funnel of its own: both the overlay row and
    ///     the Quick Access row come through this one method.
    /// </remarks>
    internal async Task<bool> SetCpuBoostFromUserAsync(CpuBoostMode mode, CancellationToken cancellationToken)
    {
        if (!await ApplyCpuBoostAsync(mode, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _cpuBoostImposed = true;
        var current = profiles.Current.Layers.Value(values => values.CpuBoost).Value;
        if (current != mode)
        {
            await profiles.SetAsync(values => values.CpuBoost = mode, $"CpuBoost={mode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Writes one mode to Windows, keeping the mode found before WSGM's first write.</summary>
    private async Task<bool> ApplyCpuBoostAsync(CpuBoostMode mode, CancellationToken cancellationToken)
    {
        if (cpuBoost is null)
        {
            return false;
        }

        try
        {
            return await Task.Run(() =>
            {
                lock (_cpuBoostGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = cpuBoost.Read();
                    if (!before.Supported)
                    {
                        if (!_cpuBoostUnsupportedLogged)
                        {
                            _cpuBoostUnsupportedLogged = true;
                            Log.Warn("Processor boost mode is not exposed by the active power scheme.");
                        }

                        PublishCpuBoost(before);
                        return false;
                    }

                    if (!_cpuBoostImposed)
                    {
                        // The value to come back to once no layer prefers one. Null when Windows holds
                        // a mode WSGM does not offer, in which case nothing is restored.
                        _cpuBoostBaseline = before.OnAc;
                    }

                    cpuBoost.Apply(mode, cancellationToken);
                    PublishCpuBoost(cpuBoost.Read());
                    return true;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Processor boost {CpuBoost.NameFor(mode)} was not applied: {ex.Message}");
            return false;
        }
    }

    private void PublishCpuBoost(CpuBoostStatus status)
    {
        var previous = _cpuBoostStatus;
        _cpuBoostStatus = status;
        if (previous != status)
        {
            CpuBoostChanged?.Invoke();
        }
    }

    /// <summary>Persists a hand-set power limit to whichever profile layer is in force.</summary>
    /// <param name="watts">The limit the user just set, already applied to the device.</param>
    /// <remarks>
    ///     Runs from the manual-power funnel, so the value has already reached the device and paused
    ///     AutoTDP. This only records it in the layer in force — the game profile while it is on, Global
    ///     otherwise — so the next launch restores it instead of the value leaking onto whatever runs next.
    /// </remarks>
    internal void PersistManualPowerLimit(int watts)
    {
        var layers = profiles.Current.Layers;
        var manual = layers.ManualTdp();
        var key = layers.PowerTargetKey;
        var current = manual is null ? null : manual.Unified ? manual.UnifiedWatts : manual.SustainedWatts;

        // The manual funnel fires on the value WSGM's own restore just wrote as well — its origin
        // keeps it out of here, but a value that already matches the layer is skipped regardless so
        // a drag that ends on the stored value writes no config.
        _profilePowerImposed = true;
        _profilePowerPaired = manual?.Unified == true;
        if (current == watts)
        {
            return;
        }

        Save(profiles.SetAsync(key.Field, watts), $"Power limit {watts} W");
    }

    /// <summary>Applies a variable-refresh state the user set.</summary>
    /// <param name="enabled">The state the user chose.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the display is now in that state.</returns>
    /// <remarks>
    ///     The user-facing counterpart to <see cref="ApplyVariableRefreshRateAsync" />, which stays the
    ///     bare device write the profile restore uses. This is what the native QAM's VRR control calls.
    ///     Saving is not done here: the write carries the user origin, so the coordinator's manual hook
    ///     runs <see cref="PersistManualVariableRefresh" /> for this path and for the overlay's Device
    ///     row alike, and one owner cannot save what the other does not.
    /// </remarks>
    internal Task<bool> SetVariableRefreshRateFromUserAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        return ApplyVariableRefreshRateAsync(
            enabled,
            CapabilityCommandOrigin.User,
            cancellationToken);
    }

    /// <summary>Saves a hand-set variable-refresh state to whichever profile layer is in force.</summary>
    /// <param name="enabled">The state the device just accepted.</param>
    /// <remarks>
    ///     Runs from the coordinator's manual funnel, so the state has already reached the device. This
    ///     only records it in the layer in force, so the next launch restores it instead of letting it
    ///     leak onto whatever runs next.
    /// </remarks>
    internal void PersistManualVariableRefresh(bool enabled)
    {
        var current = profiles.Current.Layers.Value(values => values.VariableRefreshRate).Value;
        _profileVrrImposed = true;
        if (current == enabled)
        {
            return;
        }

        Save(profiles.SetAsync(values => values.VariableRefreshRate = enabled, $"VariableRefreshRate={enabled}"),
            $"Variable refresh {(enabled ? "on" : "off")}");
    }

    /// <summary>Observes a save started from a synchronous device hook.</summary>
    private static void Save(Task save, string what)
    {
        _ = save.ContinueWith(
            task => Log.Warn(
                $"{what} was applied but could not be saved: {task.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private DeviceCapabilityView? FindPowerLimitCapability()
    {
        return readCoordinator()?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });
    }

    private DeviceCapabilityView? FindVariableRefreshCapability()
    {
        return readCoordinator()?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.VariableRefreshRate
            && view.Descriptor.SupportsWrite);
    }

    private async Task<bool> ApplyProfilePowerLimitAsync(
        DeviceCapabilityView power,
        int watts,
        CancellationToken cancellationToken,
        bool paired = false)
    {
        if (readCoordinator() is not { } coordinator)
        {
            return false;
        }

        var result = await coordinator.ExecuteCapabilityAsync(
            power.Descriptor.CapabilityId,
            power.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            TimeSpan.FromSeconds(5),
            // Not a user action: the value is already the saved preference, so it must not re-enter
            // the manual funnel and be persisted again or re-resolved into the wrong layer.
            CapabilityCommandOrigin.ProfileRestore,
            power.Projection.State.CycleGeneration,
            power.Projection.State.DescriptorGeneration,
            paired,
            cancellationToken).ConfigureAwait(false);
        var applied = paired
            ? result.Outcome == CommandOutcome.AppliedVerified && result.ReadbackValue?.IntegerValue == watts
            : result.Outcome.IsApplied();
        if (!applied)
        {
            Log.Warn(
                $"Per-application power limit {watts} W was not applied: "
                + (result.Reason?.Detail ?? result.Outcome.ToString()));
        }

        return applied;
    }

    /// <summary>Turns variable refresh rate on or off through the device plugin.</summary>
    /// <param name="enabled">The requested state.</param>
    /// <param name="origin">
    ///     Who asked. <see cref="CapabilityCommandOrigin.ProfileRestore" /> for a value WSGM is putting
    ///     back, so it is not saved again — the release case applies <see langword="false" /> when no
    ///     layer prefers a value at all, and storing that would invent a preference the user never set.
    /// </param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the device applied it.</returns>
    /// <remarks>
    ///     The plugin owns the transport — Arc Sync on the reference device — because it touches the
    ///     GPU driver, and chasing driver changes is the plugin author's burden rather than WSGM's.
    ///     This only finds the published capability and asks.
    /// </remarks>
    private async Task<bool> ApplyVariableRefreshRateAsync(
        bool enabled,
        CapabilityCommandOrigin origin,
        CancellationToken cancellationToken)
    {
        if (readCoordinator() is not { } coordinator)
        {
            return false;
        }

        var view = coordinator.Capabilities.Snapshot().FirstOrDefault(candidate =>
            candidate.Descriptor.Role is CapabilityRole.VariableRefreshRate
            && candidate.Projection.State.Available);
        if (view is null)
        {
            Log.Warn("Variable refresh rate refused: no available capability publishes it.");
            return false;
        }

        var result = await coordinator.ExecuteCapabilityAsync(
            view.Descriptor.CapabilityId,
            view.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = enabled },
            TimeSpan.FromSeconds(5),
            origin,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Verified counts, unverified counts. A timeout does not: whether the panel changed is
        // unknown, and reporting success would leave Steam's toggle disagreeing with the display.
        return result.Outcome.IsApplied();
    }
}
