using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>How close to a resume a suspend has to be before it is treated as its stale partner.</summary>
    private static readonly TimeSpan SpuriousSuspendWindow = TimeSpan.FromSeconds(2);

    /// <summary>Enough Stopwatch ticks that a session which never resumed reads as long awake.</summary>
    private static readonly long LongAgo = Stopwatch.Frequency * 3600;

    private readonly Lock _devicePowerGate = new();

    private Task _devicePowerWork = Task.CompletedTask;

    /// <summary>The direction the device cycle is heading: the last transition queued, run or not.</summary>
    private bool _deviceSuspended;

    /// <summary>When the last resume repair ran, so one wake's several resume notices repair once.</summary>
    private long _lastResumeRepairTick = long.MinValue / 2;

    /// <summary>The latest power transition queued, so an opposite one can cancel it before it runs.</summary>
    private PowerTransition? _latestPowerTransition;

    /// <summary>When the system last told this process it had resumed, on the monotonic clock.</summary>
    private long _systemResumeTimestamp = Stopwatch.GetTimestamp() - LongAgo;

    /// <summary>The same moment on the wall clock, which a sleep does not stop.</summary>
    private long _systemResumeWallTicks = DateTimeOffset.UtcNow.AddHours(-1).UtcTicks;

    private void OnSessionLocked()
    {
        QueueDevicePowerTransition(true, "session locked");
    }

    private void OnSessionUnlocked()
    {
        QueueDevicePowerTransition(false, "session unlocked");
    }

    private void OnSystemSuspending()
    {
        var monotonic = Stopwatch.GetElapsedTime(Interlocked.Read(ref _systemResumeTimestamp));
        var wall = DateTimeOffset.UtcNow
                   - new DateTimeOffset(Interlocked.Read(ref _systemResumeWallTicks), TimeSpan.Zero);
        if (IsStaleSuspend(monotonic, wall))
        {
            Log.Info(
                "Device cycle suspend skipped (system suspending): the system resumed "
                + $"{monotonic.TotalMilliseconds:F0} ms (monotonic) / {wall.TotalMilliseconds:F0} ms (wall) "
                + "ago, so this suspend belongs to a standby window that has already ended.");
            return;
        }

        // One line per sleep: both clocks are suspect across a hibernation, and the next wake has to
        // be readable from the log alone.
        Log.Info(
            $"Device cycle suspend accepted (system suspending): monotonic={monotonic.TotalMilliseconds:F0} ms, "
            + $"wall={wall.TotalMilliseconds:F0} ms since the last resume.");
        QueueDevicePowerTransition(true, "system suspending", true);
    }

    private void OnSystemResumed()
    {
        Interlocked.Exchange(ref _systemResumeTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _systemResumeWallTicks, DateTimeOffset.UtcNow.UtcTicks);
        QueueDevicePowerTransition(false, "system resumed", true);
        if (!ResumeWasUnattended())
        {
            // A wake timer or maintenance wake has nobody in front of the screen; the wake list
            // (a TV, an HDMI switch) runs when a person wakes the machine, which sends another resume.
            QueueDesktopActions(false);
        }

        RepairAfterResume();
    }

    private void OnPowerSourceChanged()
    {
        _deviceCoordinator?.OnPowerSourceChanged();
    }

    private static bool ResumeWasUnattended()
    {
        try
        {
            return ModernStandby.WasLastResumeUnattended();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Whether a suspend notification is the stale half of a wake that already happened.</summary>
    /// <param name="monotonic">Time since the last resume on the monotonic clock.</param>
    /// <param name="wall">Time since the last resume on the wall clock.</param>
    /// <returns><see langword="true" /> when the suspend must not be acted on.</returns>
    /// <remarks>
    ///     One modern standby wake delivers a resume, a suspend and a second resume within a few hundred
    ///     milliseconds; acting on the suspend in the middle tears the controller down on an awake
    ///     machine (docs\device-integration.md, "A modern standby wake must not quiesce the device").
    ///     A hibernation resume moves both clocks, forward or back, so a negative gap is discarded and
    ///     the shorter remaining gap decides.
    /// </remarks>
    internal static bool IsStaleSuspend(TimeSpan monotonic, TimeSpan wall)
    {
        TimeSpan? gap = null;
        foreach (var candidate in (ReadOnlySpan<TimeSpan>)[monotonic, wall])
        {
            if (candidate >= TimeSpan.Zero && (gap is null || candidate < gap))
            {
                gap = candidate;
            }
        }

        return gap < SpuriousSuspendWindow;
    }

    /// <summary>Re-establishes the state a sleep invalidates without announcing it.</summary>
    /// <remarks>
    ///     The device cycle has its own resume and the Steam transport reconnects on its own. What is
    ///     left are the two answers cached from before the sleep. The refresh-rate pairing is latched
    ///     on the limit it last applied, so a panel that came back at its default rate keeps the
    ///     paired label while running at another cadence until the cap is moved twice. RTSS is only
    ///     probed while a UI client holds an observation lease, so with Quick Access closed and
    ///     Steam's performance rows absent nothing looks at it after a wake at all: a restarted RTSS
    ///     or a profile edited meanwhile goes unnoticed until the next game launch. One forced pass
    ///     of each costs a driver round trip and a probe, and only on a real wake.
    /// </remarks>
    private void RepairAfterResume()
    {
        var now = Environment.TickCount64;
        if (_shutdownRequested || _overlayTestOnly || now - _lastResumeRepairTick < 2000)
        {
            return;
        }

        _lastResumeRepairTick = now;

        ApplyRefreshPairing(_performance?.Current.Desired.FrameLimit ?? 0, true);
        if (_performance is { } performance && PerformanceEnabled(_config))
        {
            Log.Observe(performance.RefreshAsync(), "RTSS resume refresh");
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
    private void QueueDevicePowerTransition(bool suspend, string reason, bool systemSleep = false)
    {
        if (_shutdownRequested)
        {
            return;
        }

        var coordinator = _deviceCoordinator;
        if (coordinator is null && _commonPlugins is null)
        {
            Log.Info(
                $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): no "
                + "device coordinator is active.");
            return;
        }

        lock (_devicePowerGate)
        {
            // A cycle that faulted across the sleep still needs this wake's resume, even though no
            // suspend was recorded for it.
            var repair = !suspend && systemSleep && coordinator?.State is DeviceCycleState.Faulted;
            if (_deviceSuspended == suspend && !repair)
            {
                Log.Info(
                    $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): the "
                    + $"cycle is already {(suspend ? "suspended or suspending" : "running or resuming")}.");
                return;
            }

            _deviceSuspended = suspend;
            if (_latestPowerTransition is { Started: false, Cancelled: false } queued && queued.Suspend != suspend)
            {
                // The opposite edge never started, so the two cancel out and the cycle stays as it is:
                // running a suspend queued before the freeze on the woken machine would tear it down.
                queued.Cancelled = true;
                _latestPowerTransition = null;
                Log.Info($"Device cycle {(suspend ? "suspend" : "resume")} ({reason}) cancels the queued "
                         + $"{queued.Reason}.");
                return;
            }

            PowerTransition transition = new(suspend, systemSleep, reason);
            _latestPowerTransition = transition;
            var previous = _devicePowerWork;
            _devicePowerWork = Task.Run(() => ApplyDevicePowerTransitionAsync(previous, coordinator, transition));
        }
    }

    private async Task ApplyDevicePowerTransitionAsync(
        Task previous,
        DeviceCoordinator? coordinator,
        PowerTransition transition)
    {
        // Never faults: the continuation below reports its own failures and returns normally, so
        // awaiting the previous transition cannot throw here.
        await previous.ConfigureAwait(false);
        lock (_devicePowerGate)
        {
            if (transition.Cancelled)
            {
                return;
            }

            transition.Started = true;
        }

        var suspend = transition.Suspend;
        try
        {
            var deviceWork = coordinator is null ? Task.CompletedTask
                : suspend ? coordinator.SuspendAsync() : coordinator.ResumeAsync(transition.SystemSleep);
            await Task.WhenAll(deviceWork, ApplyCommonPluginPowerAsync(suspend)).ConfigureAwait(false);
            Log.Info($"Device cycle {(suspend ? "suspended" : "resumed")}: {transition.Reason}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_devicePowerGate)
            {
                // Recorded as suspended whichever direction failed, so the next resume always runs:
                // a missed resume leaves the device down, a redundant suspend edge is harmless.
                if (ReferenceEquals(_latestPowerTransition, transition))
                {
                    _deviceSuspended = true;
                }
            }

            Log.Error($"Device cycle {(suspend ? "suspend" : "resume")} failed ({transition.Reason})", ex);
        }
    }

    private async Task ApplyCommonPluginPowerAsync(bool suspend)
    {
        try
        {
            if (_commonPlugins is { } manager)
            {
                await manager.PowerTransitionAsync(suspend, _shutdownCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin power transition failed", ex);
        }
    }

    /// <summary>One queued device power transition.</summary>
    private sealed class PowerTransition(bool suspend, bool systemSleep, string reason)
    {
        internal bool Cancelled;
        internal bool Started;
        internal bool Suspend { get; } = suspend;
        internal bool SystemSleep { get; } = systemSleep;
        internal string Reason { get; } = reason;
    }
}
