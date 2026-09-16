using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Carries the running application's per-game power limit and variable refresh preference
/// to the device, and saves values set by hand to the profile layer in force.</summary>
/// <remarks>The session decides when this runs; this remembers what it imposed, so a value one
/// application set is undone when the next application does not ask for it.</remarks>
/// <param name="readConfig">Reads the current configuration, which a reload replaces.</param>
/// <param name="readCoordinator">Reads the device coordinator, or null without device integration.</param>
/// <param name="readPerformance">Reads the RTSS performance service, or null before it starts.</param>
/// <param name="readAutoTdp">Reads AutoTDP, or null when it is not running.</param>
internal sealed class ApplicationPerformanceReconciler(
    Func<AppConfig> readConfig,
    Func<DeviceCoordinator?> readCoordinator,
    Func<PerformanceService?> readPerformance,
    Func<AutoTdpService?> readAutoTdp)
{
    private bool _profilePowerImposed;
    private bool _profilePowerPaired;
    private bool _profileVrrImposed;
    private string _lastReconciledApplicationId = "(uninitialised)";

    /// <summary>
    /// Restores the power limit and variable-refresh state the incoming application prefers, and takes
    /// back the ones the outgoing application imposed.
    /// </summary>
    /// <param name="applicationId">The canonical identity of the application now in front, or null.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    /// The fix for a power limit or refresh mode set in a game leaking onto the desktop after the game
    /// closes. The per-game switch governs every performance value, so an application's own value
    /// applies only while its profile is enabled; otherwise it inherits the global one. When neither
    /// layer prefers a value, the outgoing application's is undone rather than left running — for
    /// power, automatic control resumes if it is on and the limit is otherwise released to the device
    /// ceiling; for variable refresh, it returns to off — but only when WSGM actually imposed the
    /// current one, so a session that never used the feature is never touched. Both decisions are pure
    /// and tested (<see cref="PerApplicationPowerPolicy"/>, <see cref="PerApplicationVrrPolicy"/>);
    /// this only reads the layers and carries them out.
    /// </remarks>
    internal async Task ReconcileApplicationProfileAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        // The snapshot bumps when the foreground executable is enriched for the same running game;
        // a profile belongs to the application, not the focused window, so reconcile only when the
        // identity actually changes. A mid-game change reaches the device through the manual funnels,
        // not here.
        var identityKey = applicationId ?? string.Empty;
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

        var entry = readConfig().Performance.FindApplication(applicationId);
        var perGameActive = entry?.UsePerGameProfile ?? false;

        if (power is not null && !coordinator.PowerAssignments.HasCurrentAssignment)
        {
            await ReconcileApplicationPowerLimitAsync(
                power,
                coordinator,
                entry,
                perGameActive,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }

        if (vrr is not null)
        {
            await ReconcileApplicationVariableRefreshAsync(
                entry,
                perGameActive,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileApplicationPowerLimitAsync(
        DeviceCapabilityView power,
        DeviceCoordinator coordinator,
        PerformanceApplicationConfig? entry,
        bool perGameActive,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        var (effective, paired) = ManualTdpPolicy.ResolveTarget(readConfig().Performance, entry, perGameActive);
        var manualProfile = ManualTdpPolicy.Resolve(readConfig().Performance, entry, perGameActive);
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
                var applied = splitPair
                    ? await coordinator.RestoreSplitPowerAsync(power, decision.Watts, manualProfile!.BoostWatts!.Value,
                        cancellationToken).ConfigureAwait(false)
                    : await ApplyProfilePowerLimitAsync(power, decision.Watts, cancellationToken, paired).ConfigureAwait(false);
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
        PerformanceApplicationConfig? entry,
        bool perGameActive,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        var effective = PerApplicationVrrPolicy.ResolveEffective(
            readConfig().Performance.VariableRefreshRate,
            entry?.VariableRefreshRate,
            perGameActive);
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

    /// <summary>Persists a hand-set power limit to whichever profile layer is in force.</summary>
    /// <param name="watts">The limit the user just set, already applied to the device.</param>
    /// <remarks>
    /// Runs from the manual-power funnel, so the value has already reached the device and paused
    /// AutoTDP. This only records it as the user's preference for the running application's layer —
    /// its own when a per-game profile is enabled, the global layer otherwise — so the next launch
    /// restores it instead of the value leaking onto whatever runs next.
    /// </remarks>
    internal void PersistManualPowerLimit(int watts)
    {
        var (applicationId, entry, applicationLayer) = ActivePerformanceLayer();
        var current = applicationLayer ? entry!.TdpWatts : readConfig().Performance.TdpWatts;
        var manual = ManualTdpPolicy.Resolve(readConfig().Performance, entry, applicationLayer);
        if (manual is not null) { current = manual.Unified ? manual.UnifiedWatts : manual.SustainedWatts; }

        // The manual funnel fires on the value WSGM's own restore just wrote as well — its origin
        // keeps it out of here, but a value that already matches the layer is skipped regardless so
        // a drag that ends on the stored value writes no config.
        _profilePowerImposed = true;
        _profilePowerPaired = manual?.Unified == true;
        if (current == watts)
        {
            return;
        }

        SaveToPerformanceLayer(
            applicationId,
            applicationLayer,
            target =>
            {
                if (manual is null) { target.TdpWatts = watts; }
                else { target.ManualTdp = ManualTdpPolicy.WithTarget(manual, watts); }
            },
            global =>
            {
                if (manual is null) { global.TdpWatts = watts; }
                else { global.ManualTdp = ManualTdpPolicy.WithTarget(manual, watts); }
            },
            $"Power limit {watts} W");
    }

    /// <summary>Applies a variable-refresh state the user set.</summary>
    /// <param name="enabled">The state the user chose.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the display is now in that state.</returns>
    /// <remarks>
    /// The user-facing counterpart to <see cref="ApplyVariableRefreshRateAsync"/>, which stays the
    /// bare device write the profile restore uses. This is what the native QAM's VRR control calls.
    /// Saving is not done here: the write carries the user origin, so the coordinator's manual hook
    /// runs <see cref="PersistManualVariableRefresh"/> for this path and for the overlay's Device
    /// row alike, and one owner cannot save what the other does not.
    /// </remarks>
    internal Task<bool> SetVariableRefreshRateFromUserAsync(
        bool enabled,
        CancellationToken cancellationToken) => ApplyVariableRefreshRateAsync(
            enabled,
            CapabilityCommandOrigin.User,
            cancellationToken);

    /// <summary>Saves a hand-set variable-refresh state to whichever profile layer is in force.</summary>
    /// <param name="enabled">The state the device just accepted.</param>
    /// <remarks>
    /// Runs from the coordinator's manual funnel, so the state has already reached the device. This
    /// only records it as the running application's preference — its own layer when a per-game
    /// profile is enabled, the global layer otherwise — so the next launch restores it instead of
    /// letting it leak onto whatever runs next.
    /// </remarks>
    internal void PersistManualVariableRefresh(bool enabled)
    {
        var (applicationId, entry, applicationLayer) = ActivePerformanceLayer();
        var current = applicationLayer ? entry!.VariableRefreshRate : readConfig().Performance.VariableRefreshRate;

        _profileVrrImposed = true;
        if (current == enabled)
        {
            return;
        }

        SaveToPerformanceLayer(
            applicationId,
            applicationLayer,
            target => target.VariableRefreshRate = enabled,
            global => global.VariableRefreshRate = enabled,
            $"Variable refresh {(enabled ? "on" : "off")}");
    }

    /// <summary>The running application's performance entry and whether its own layer is in force.</summary>
    private (string? ApplicationId, PerformanceApplicationConfig? Entry, bool ApplicationLayer) ActivePerformanceLayer()
    {
        var applicationId = readPerformance()?.Current.Target?.ApplicationId;
        var entry = readConfig().Performance.FindApplication(applicationId);
        return (applicationId, entry, entry is { UsePerGameProfile: true });
    }

    /// <summary>Saves a hand-set preference to the application layer when it is in force, else globally.</summary>
    /// <param name="applicationId">The running application.</param>
    /// <param name="applicationLayer">Whether its own layer was in force when the value was set.</param>
    /// <param name="application">Writes the value to the application's entry.</param>
    /// <param name="global">Writes the value to the global layer.</param>
    /// <param name="saved">What was saved, for the log.</param>
    private static void SaveToPerformanceLayer(
        string? applicationId,
        bool applicationLayer,
        Action<PerformanceApplicationConfig> application,
        Action<PerformanceConfig> global,
        string saved)
    {
        ConfigStore.Mutate(config =>
        {
            if (applicationLayer && config.Performance.FindApplication(applicationId) is { } target)
            {
                application(target);
                return;
            }

            global(config.Performance);
        });
        Log.Info($"{saved} saved to the " + (applicationLayer ? $"profile for {applicationId}." : "global profile."));
    }

    private DeviceCapabilityView? FindPowerLimitCapability() =>
        readCoordinator()?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });

    private DeviceCapabilityView? FindVariableRefreshCapability() =>
        readCoordinator()?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.VariableRefreshRate
            && view.Descriptor.SupportsWrite);

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
            cancellationToken,
            expectedCycle: power.Projection.State.CycleGeneration,
            expectedDescriptors: power.Projection.State.DescriptorGeneration,
            applyPowerPair: paired).ConfigureAwait(false);
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
    /// Who asked. <see cref="CapabilityCommandOrigin.ProfileRestore"/> for a value WSGM is putting
    /// back, so it is not saved again — the release case applies <see langword="false"/> when no
    /// layer prefers a value at all, and storing that would invent a preference the user never set.
    /// </param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the device applied it.</returns>
    /// <remarks>
    /// The plugin owns the transport — Arc Sync on the reference device — because it touches the
    /// GPU driver, and chasing driver changes is the plugin author's burden rather than WSGM's.
    /// This only finds the published capability and asks.
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
            cancellationToken).ConfigureAwait(false);

        // Verified counts, unverified counts. A timeout does not: whether the panel changed is
        // unknown, and reporting success would leave Steam's toggle disagreeing with the display.
        return result.Outcome.IsApplied();
    }
}
