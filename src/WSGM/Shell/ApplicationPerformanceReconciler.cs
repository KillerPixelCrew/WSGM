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
///     application set is undone when the next application does not ask for it. The power limit
///     itself is carried out by the device coordinator inside its power lane, which also owns the
///     record of what it imposed.
/// </remarks>
/// <param name="profiles">The profile owner the values are read from and saved to.</param>
/// <param name="readCoordinator">Reads the device coordinator, or null without device integration.</param>
/// <param name="cpuBoost">Windows' processor boost mode, or null when this session must not write it.</param>
/// <param name="readGpu">
///     Reads the graphics coordinator, whose packages publish variable refresh with or without device
///     integration; null when the session has none.
/// </param>
internal sealed class ApplicationPerformanceReconciler(
    ProfileService profiles,
    Func<DeviceCoordinator?> readCoordinator,
    CpuBoost? cpuBoost = null,
    Func<GpuCoordinator?>? readGpu = null)
{
    /// <summary>Guards every processor boost field and serializes the Windows write.</summary>
    private readonly Lock _cpuBoostGate = new();

    private readonly ApplicationReconcileKeys _reconciled = new();

    /// <summary>Guards <see cref="_profileVrrImposed" />, written from the fan-out and the manual funnel.</summary>
    private readonly Lock _vrrGate = new();

    private CpuBoostMode? _cpuBoostBaseline;
    private bool _cpuBoostImposed;
    private volatile CpuBoostStatus? _cpuBoostStatus;
    private bool _cpuBoostUnsupportedLogged;
    private (string? ApplicationId, CpuBoostMode? Mode)? _lastReconciledCpuBoost;
    private bool _profileVrrImposed;

    /// <summary>Whether this session can read and write the processor boost mode at all.</summary>
    internal bool CpuBoostAvailable => cpuBoost is not null;

    /// <summary>The last processor boost readback or written mode, or null before the first read.</summary>
    internal CpuBoostStatus? CpuBoostStatus => _cpuBoostStatus;

    /// <summary>Raised after the processor boost status changes, from whichever thread changed it.</summary>
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
        // the capability keys below are deliberately not recorded while their capability is missing.
        await ReconcileApplicationCpuBoostAsync(
            layers.Value(values => values.CpuBoost).Value,
            applicationId,
            cancellationToken).ConfigureAwait(false);

        var coordinator = readCoordinator();
        var power = FindPowerLimitCapability(coordinator);
        var vrr = FindVariableRefreshCapability();
        // Each value keeps its own identity and records it only once its capability was there to take
        // it. A graphics package publishing variable refresh before the device package publishes its
        // power limit must not mark the power limit reconciled for this application.
        var due = _reconciled.Take(
            applicationId,
            manual,
            vrrPreference,
            power is not null && coordinator is not null,
            vrr is not null);

        if (due.Power && coordinator is not null)
        {
            await coordinator.ReconcileApplicationPowerAsync(manual, applicationId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (due.VariableRefresh)
        {
            await ReconcileApplicationVariableRefreshAsync(
                vrrPreference,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileApplicationVariableRefreshAsync(
        bool? effective,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        bool imposed;
        lock (_vrrGate)
        {
            imposed = _profileVrrImposed;
        }

        var decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effective,
            imposed);
        if (decision.Action is not PerAppVrrAction.Apply)
        {
            return;
        }

        if (await ApplyVariableRefreshRateAsync(
                decision.Enabled,
                CapabilityCommandOrigin.ProfileRestore,
                cancellationToken).ConfigureAwait(false))
        {
            lock (_vrrGate)
            {
                _profileVrrImposed = effective is not null;
            }

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

        PerAppCpuBoostDecision decision;
        lock (_cpuBoostGate)
        {
            (string? ApplicationId, CpuBoostMode? Mode) key = (applicationId, effective);
            if (_lastReconciledCpuBoost == key)
            {
                return;
            }

            _lastReconciledCpuBoost = key;
            decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(
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
        }

        if (await ApplyCpuBoostAsync(decision.Mode, effective is not null, cancellationToken).ConfigureAwait(false))
        {
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
    /// <returns>Whether the mode was written.</returns>
    /// <remarks>
    ///     The user-facing counterpart to the transition's own write. Saving happens here rather than
    ///     in a device hook, because Windows has no manual funnel of its own: both the overlay row and
    ///     the Quick Access row come through this one method.
    /// </remarks>
    internal async Task<bool> SetCpuBoostFromUserAsync(CpuBoostMode mode, CancellationToken cancellationToken)
    {
        if (!await ApplyCpuBoostAsync(mode, true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var current = profiles.Current.Layers.Value(values => values.CpuBoost).Value;
        if (current != mode)
        {
            await profiles.SetAsync(values => values.CpuBoost = mode, $"CpuBoost={mode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Writes one mode to Windows, keeping the mode found before WSGM's first write.</summary>
    /// <param name="mode">The mode to write.</param>
    /// <param name="imposed">
    ///     Whether WSGM owns the mode afterwards, so a later application without a preference restores the
    ///     baseline. Recorded under the same gate as the write.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the mode was written.</returns>
    private async Task<bool> ApplyCpuBoostAsync(CpuBoostMode mode, bool imposed, CancellationToken cancellationToken)
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
                    _cpuBoostImposed = imposed;
                    PublishCpuBoost(new CpuBoostStatus(true, mode, mode));
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
        lock (_vrrGate)
        {
            _profileVrrImposed = true;
        }

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

    private static DeviceCapabilityView? FindPowerLimitCapability(DeviceCoordinator? coordinator)
    {
        return coordinator?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });
    }

    private PublishedCapability? FindVariableRefreshCapability()
    {
        return VariableRefreshCapabilities.Find(readCoordinator(), readGpu?.Invoke(), false);
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
    ///     A graphics package owns the transport, because it touches the GPU driver, and chasing driver
    ///     changes is the plugin author's burden rather than WSGM's. This only finds the published
    ///     capability, on whichever package publishes it, and asks.
    /// </remarks>
    private async Task<bool> ApplyVariableRefreshRateAsync(
        bool enabled,
        CapabilityCommandOrigin origin,
        CancellationToken cancellationToken)
    {
        var device = readCoordinator();
        var gpu = readGpu?.Invoke();
        if (VariableRefreshCapabilities.Find(device, gpu, true) is not { } target)
        {
            Log.Warn("Variable refresh rate refused: no available capability publishes it.");
            return false;
        }

        var result = await VariableRefreshCapabilities.ExecuteAsync(target, device, gpu, enabled, origin,
            cancellationToken).ConfigureAwait(false);

        // Verified counts, unverified counts. A timeout does not: whether the panel changed is
        // unknown, and reporting success would leave Steam's toggle disagreeing with the display.
        return result.Outcome.IsApplied();
    }
}

/// <summary>Which per-application values a pass owes, and for which application it last carried each.</summary>
/// <remarks>
///     Keyed on what resolves rather than on the snapshot generation, and kept per capability: a key is
///     recorded only when its capability was published for the pass, so a capability that appears later
///     still reconciles on the next pass for the same application.
/// </remarks>
internal sealed class ApplicationReconcileKeys
{
    // Record value equality, not generated ToString text, decides whether a value changed.
    private (string? ApplicationId, ManualTdpProfile? Manual)? _power;
    private (string? ApplicationId, bool? VariableRefresh)? _variableRefresh;

    /// <summary>Decides which values this pass carries and records them as carried.</summary>
    /// <param name="applicationId">The running application, or null for the desktop.</param>
    /// <param name="manual">The power limit the layers resolve to, or null.</param>
    /// <param name="variableRefresh">The variable-refresh preference the layers resolve to, or null.</param>
    /// <param name="powerPublished">Whether a writable power limit is published right now.</param>
    /// <param name="variableRefreshPublished">Whether variable refresh is published right now.</param>
    /// <returns>Which values the pass reconciles.</returns>
    internal (bool Power, bool VariableRefresh) Take(
        string? applicationId,
        ManualTdpProfile? manual,
        bool? variableRefresh,
        bool powerPublished,
        bool variableRefreshPublished)
    {
        (string? ApplicationId, ManualTdpProfile? Manual) powerKey = (applicationId, manual);
        (string? ApplicationId, bool? VariableRefresh) variableRefreshKey = (applicationId, variableRefresh);
        var power = powerPublished && _power != powerKey;
        var refresh = variableRefreshPublished && _variableRefresh != variableRefreshKey;
        if (power)
        {
            _power = powerKey;
        }

        if (refresh)
        {
            _variableRefresh = variableRefreshKey;
        }

        return (power, refresh);
    }
}
