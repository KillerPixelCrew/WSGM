using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>
///     Serializes the device cycle's suspend and resume edges, with the built-in GPU drivers and the
///     common plugins, so each runs after the previous one and an opposite edge that never started
///     cancels it.
/// </summary>
/// <param name="integrations">The owners every transition reaches.</param>
/// <param name="state">The session's live state, whose shutdown drops queued transitions.</param>
internal sealed class DevicePowerQueue(IntegrationServices integrations, SessionState state)
{
    private readonly Lock _gate = new();

    /// <summary>The direction the device cycle is heading: the last transition queued, run or not.</summary>
    private bool _deviceSuspended;
    private bool _fanDisplaySleeping;
    private bool _displayPowerSuspended;

    /// <summary>The latest power transition queued, so an opposite one can cancel it before it runs.</summary>
    private DevicePowerTransition? _latestTransition;

    private Task _work = Task.CompletedTask;

    /// <summary>
    ///     The queue's tail, which completes once every queued transition has run or been dropped. It
    ///     never faults, so shutdown can join it within its deadline before the device stops.
    /// </summary>
    internal Task Work
    {
        get
        {
            lock (_gate)
            {
                return _work;
            }
        }
    }

    /// <summary>Quiesces or revives the device cycle with the session it belongs to.</summary>
    /// <param name="suspend">Whether the cycle should quiesce.</param>
    /// <param name="reason">The notification that asked for it, for the log.</param>
    /// <param name="systemSleep">Whether the notification is a system suspend or resume, not a lock.</param>
    /// <remarks>
    ///     Edge-triggered and serialized, because the four notifications overlap: a sleep started from
    ///     the lock screen delivers a lock and a suspend, and Windows sends both resume events for one
    ///     wake. Neither coordinator call is idempotent — resume advances the cycle generation — so
    ///     only a real transition is forwarded, and each one waits for the previous to finish.
    /// </remarks>
    internal void Queue(bool suspend, string reason, bool systemSleep = false)
    {
        var coordinator = integrations.Device;
        if (!integrations.HasPowerOwners)
        {
            Log.Info(
                $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): no "
                + "device coordinator is active.");
            return;
        }

        lock (_gate)
        {
            // Checked under the gate, so the tail shutdown joins is the last transition ever queued.
            if (state.ShutdownRequested)
            {
                return;
            }

            if (!suspend && _displayPowerSuspended)
            {
                Log.Info($"Device resume deferred ({reason}): Modern Standby display is still off.");
                return;
            }

            // A cycle that faulted across the sleep still needs this wake's resume, even though no
            // suspend was recorded for it.
            var repair = !suspend && systemSleep && coordinator?.State is DeviceCycleState.Faulted;
            if (_deviceSuspended == suspend && !repair)
            {
                Log.Info(
                    $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): the "
                    + $"cycle is already {(suspend ? "suspended or suspending" : "running or resuming")}.");
                RestoreLimitsIfWake(coordinator, suspend, systemSleep);
                return;
            }

            _deviceSuspended = suspend;
            if (_latestTransition is { Started: false, Cancelled: false } queued && queued.Suspend != suspend)
            {
                // The opposite edge never started, so the two cancel out and the cycle stays as it is:
                // running a suspend queued before the freeze on the woken machine would tear it down.
                queued.Cancelled = true;
                _latestTransition = null;
                Log.Info($"Device cycle {(suspend ? "suspend" : "resume")} ({reason}) cancels the queued "
                         + $"{queued.Reason}.");
                if (!repair)
                {
                    RestoreLimitsIfWake(coordinator, suspend, systemSleep);
                    return;
                }
            }

            DevicePowerTransition transition = new(suspend, systemSleep, reason);
            _latestTransition = transition;
            var previous = _work;
            _work = Task.Run(() => ApplyAsync(previous, coordinator, transition));
        }
    }

    /// <summary>Hands Modern Standby ownership back early; classic display sleep only holds software fans.</summary>
    internal void QueueDisplaySleep(bool sleeping, bool modernStandby)
    {
        lock (_gate)
        {
            if (state.ShutdownRequested || _fanDisplaySleeping == sleeping)
            {
                return;
            }

            _fanDisplaySleeping = sleeping;
            _displayPowerSuspended = sleeping && modernStandby;
            var previous = _work;
            _work = Task.Run(() => ApplyFanDisplayStateAsync(previous, sleeping, modernStandby));
            if (modernStandby && !sleeping && _deviceSuspended)
            {
                // A real system suspend must resume the physical input before the virtual pad returns.
                Queue(false, "Modern Standby display lit", systemSleep: true);
            }

            if (modernStandby)
            {
                var beforeController = _work;
                _work = Task.Run(() => ApplyControllerDisplayStateAsync(beforeController, sleeping));
            }
        }
    }

    private async Task ApplyControllerDisplayStateAsync(Task previous, bool sleeping)
    {
        await previous.ConfigureAwait(false);
        if (state.ShutdownRequested || integrations.Device is not { } coordinator)
        {
            return;
        }

        try
        {
            await coordinator.SetDisplayControllerSleepingAsync(sleeping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Virtual controller display power transition failed", ex);
        }
    }

    private async Task ApplyFanDisplayStateAsync(Task previous, bool sleeping, bool modernStandby)
    {
        await previous.ConfigureAwait(false);
        if (state.ShutdownRequested)
        {
            return;
        }

        if (modernStandby && integrations.BuiltinGpu is { } graphics)
        {
            try
            {
                await graphics.PowerTransitionAsync(sleeping, state.ShutdownToken, displayOnly: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (state.ShutdownRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Graphics display sleep transition failed", ex);
            }
        }

        if (integrations.Device is not { } coordinator)
        {
            return;
        }

        try
        {
            await coordinator.FanCurves.Controller.SetDisplaySleepingAsync(sleeping).ConfigureAwait(false);
            if (modernStandby)
            {
                await coordinator.SetDisplayPowerSleepingAsync(sleeping).ConfigureAwait(false);
            }

            Log.Info($"Software fan display-off hold {(sleeping ? "applied" : "released")}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Software fan display power transition failed", ex);
        }
    }

    /// <summary>
    ///     A wake whose cycle does not restart still needs its power limits written again: Windows can
    ///     deliver the suspend only after the freeze, right before the resume, and firmware has reset by then.
    /// </summary>
    private static void RestoreLimitsIfWake(DeviceCoordinator? coordinator, bool suspend, bool systemSleep)
    {
        if (!suspend && systemSleep)
        {
            coordinator?.RestoreLimitsAfterWake();
        }
    }

    /// <summary>
    ///     Drops a transition that has not started, so it never runs, and returns the tail to join: the
    ///     device stops after it either way.
    /// </summary>
    /// <returns>The queue's tail.</returns>
    internal Task CloseForShutdown()
    {
        lock (_gate)
        {
            if (_latestTransition is { Started: false } queued)
            {
                queued.Cancelled = true;
            }

            return _work;
        }
    }

    private static async Task TransitionDeviceAsync(DeviceCoordinator? coordinator, bool suspend, bool systemSleep)
    {
        if (coordinator is null)
        {
            return;
        }

        if (suspend)
        {
            // Finish an admitted AutoTDP command before firmware takes the limits back.
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await coordinator.Power.AutoTdp.SuspendControlAsync(drain.Token).ConfigureAwait(false);
            await coordinator.SuspendAsync().ConfigureAwait(false);
        }
        else
        {
            await coordinator.ResumeAsync(systemSleep).ConfigureAwait(false);
            // ResumeAsync releases the device transition lane before its desired-state pass runs.
            // Keep automatic writes held until the selected path and profile are restored.
            await coordinator.ResumeRestore.ConfigureAwait(false);
            coordinator.Power.AutoTdp.ResumeControl();
        }
    }

    private async Task ApplyAsync(
        Task previous,
        DeviceCoordinator? coordinator,
        DevicePowerTransition transition)
    {
        // Never faults: the continuation below reports its own failures and returns normally, so
        // awaiting the previous transition cannot throw here.
        await previous.ConfigureAwait(false);
        lock (_gate)
        {
            // A transition still waiting when shutdown began is dropped: the device is about to stop,
            // and the one already running is what shutdown waits for.
            if (transition.Cancelled || state.ShutdownRequested)
            {
                return;
            }

            transition.Started = true;
        }

        var suspend = transition.Suspend;
        try
        {
            var deviceWork = TransitionDeviceAsync(coordinator, suspend, transition.SystemSleep);
            await Task.WhenAll(deviceWork, integrations.PluginPowerTransitionAsync(suspend)).ConfigureAwait(false);
            Log.Info($"Device cycle {(suspend ? "suspended" : "resumed")}: {transition.Reason}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                // Recorded as suspended whichever direction failed, so the next resume always runs:
                // a missed resume leaves the device down, a redundant suspend edge is harmless.
                if (ReferenceEquals(_latestTransition, transition))
                {
                    _deviceSuspended = true;
                }
            }

            Log.Error($"Device cycle {(suspend ? "suspend" : "resume")} failed ({transition.Reason})", ex);
        }
    }
}
