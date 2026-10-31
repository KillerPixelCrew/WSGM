using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Projects the one canonical Steam running-application identity into every per-application
///     consumer: the profile store, whose change drives RTSS, the device, power and refresh, and the
///     managed controller target. Rapid transitions coalesce to the latest identity and never retain
///     an executable after Steam reports exit, ambiguity, or loss of observation.
/// </summary>
/// <remarks>
///     Both consumers receive the same snapshot from one observation lease; do not add independent CEF polling.
/// </remarks>
internal sealed class RunningApplicationCoordinator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly IRunningApplicationTargetSource _monitor;

    private readonly Func<RunningApplicationTargetSnapshot, CancellationToken, Task>?
        _setControllerTargetAsync;

    private readonly Action<PerformanceApplicationTarget?> _setTarget;
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _activeApply;
    private bool _disposed;
    private long _latestGeneration = -1;
    private IDisposable? _observation;
    private RunningApplicationTargetSnapshot? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;

    /// <summary>Subscribes to identity changes and starts applying the current snapshot.</summary>
    /// <param name="monitor">Borrowed shared source; this coordinator owns only its observation lease.</param>
    /// <param name="setTarget">Tells the profile store which application runs; null clears it.</param>
    /// <param name="setControllerTargetAsync">Optional controller policy consumer of the same snapshot.</param>
    internal RunningApplicationCoordinator(
        IRunningApplicationTargetSource monitor,
        Action<PerformanceApplicationTarget?> setTarget,
        Func<RunningApplicationTargetSnapshot, CancellationToken, Task>? setControllerTargetAsync = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _setTarget = setTarget ?? throw new ArgumentNullException(nameof(setTarget));
        _setControllerTargetAsync = setControllerTargetAsync;
        _monitor.Changed += OnTargetChanged;
        try
        {
            _observation = _monitor.AcquireObservation();
            Queue(_monitor.Current);
        }
        catch
        {
            _monitor.Changed -= OnTargetChanged;
            _observation?.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    /// <summary>Unsubscribes, cancels and joins pending work, then clears the running application.</summary>
    /// <returns>
    ///     Completion after cleanup is attempted; the borrowed monitor and controller services remain owned by the
    ///     session.
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        CancellationTokenSource? activeApply;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            worker = _worker;
            activeApply = _activeApply;
        }

        _monitor.Changed -= OnTargetChanged;
        _observation?.Dispose();
        _observation = null;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        TryCancel(activeApply);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            _setTarget(null);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Profile running-application target cleanup failed: {ex.Message}");
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private static async Task ApplyControllerAsync(
        Func<RunningApplicationTargetSnapshot, CancellationToken, Task> applyController,
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await applyController(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn(
                "Controller running-application target apply failed for generation "
                + $"{snapshot.Generation}: {ex.Message}");
        }
    }

    private void ApplyProfile(RunningApplicationTargetSnapshot snapshot)
    {
        try
        {
            _setTarget(Project(snapshot));
        }
        catch (Exception ex)
        {
            Log.Warn(
                "Profile running-application target apply failed for generation "
                + $"{snapshot.Generation}: {ex.Message}");
        }
    }

    /// <summary>Projects only an unambiguous application identity for performance policy.</summary>
    /// <param name="snapshot">Current identity evidence.</param>
    /// <returns>An active or identity-only target; null for global, ambiguous or unavailable state.</returns>
    internal static PerformanceApplicationTarget? Project(
        RunningApplicationTargetSnapshot snapshot)
    {
        return snapshot.State is RunningApplicationTargetState.Active
                   or RunningApplicationTargetState.IdentityOnly
               && snapshot.ApplicationId is { Length: > 0 } applicationId
            ? new PerformanceApplicationTarget(
                applicationId,
                snapshot.SteamAppId,
                snapshot.RtssProfileName)
            : null;
    }

    /// <summary>Whether a delivered snapshot predates the newest one already accepted.</summary>
    /// <param name="latestGeneration">Newest local revision already accepted.</param>
    /// <param name="snapshot">Incoming snapshot to compare.</param>
    /// <returns>True only for a strictly older revision; equal revisions may be reapplied.</returns>
    internal static bool IsOlder(long latestGeneration, RunningApplicationTargetSnapshot snapshot)
    {
        return snapshot.Generation < latestGeneration;
    }

    /// <summary>Queues the current snapshot again, coalescing with pending identity updates.</summary>
    internal void RefreshCurrent()
    {
        Queue(_monitor.Current);
    }

    private void OnTargetChanged(RunningApplicationTargetSnapshot snapshot)
    {
        Queue(snapshot);
    }

    private void Queue(RunningApplicationTargetSnapshot snapshot)
    {
        CancellationTokenSource? activeApply;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (IsOlder(_latestGeneration, snapshot))
            {
                Log.Info(
                    $"Running-application generation {snapshot.Generation} was ignored because "
                    + $"generation {_latestGeneration} is already queued or applying.");
                return;
            }

            _latestGeneration = snapshot.Generation;
            _pending = snapshot;
            activeApply = _activeApply;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(ApplyPendingAsync, CancellationToken.None);
            }
        }

        // Cancel outside the gate because cancellation callbacks are external code. The pending
        // snapshot was installed while holding the gate, so the worker can no longer start an old
        // controller dispatch even if this cancellation races its async continuation.
        TryCancel(activeApply);
    }

    /// <summary>
    ///     Starts controller and power reconciliation only while this snapshot is still current.
    /// </summary>
    /// <remarks>
    ///     The check and delegate invocation share the queue gate. This gives queueing, disposal, and
    ///     controller dispatch one ordering point instead of leaving a race between a separate
    ///     supersession check and the call. The delegate's later async work carries the per-snapshot
    ///     cancellation token and is retired as soon as a newer snapshot is queued.
    /// </remarks>
    private Task? StartControllerApply(
        RunningApplicationTargetSnapshot snapshot,
        CancellationTokenSource applyCancellation)
    {
        lock (_gate)
        {
            if (_pending is not null || _disposed || applyCancellation.IsCancellationRequested)
            {
                return null;
            }

            return _setControllerTargetAsync is { } applyController
                ? ApplyControllerAsync(applyController, snapshot, applyCancellation.Token)
                : Task.CompletedTask;
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed and released this retired generation between the queue update
            // and cancellation. There is no work left for it to stop.
        }
    }

    private async Task ApplyPendingAsync()
    {
        while (true)
        {
            RunningApplicationTargetSnapshot? snapshot;
            CancellationTokenSource applyCancellation;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot is null || _disposed)
                {
                    _workerRunning = false;
                    return;
                }

                applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _activeApply = applyCancellation;
            }

            try
            {
                // Each consumer is applied independently: a profile failure must not leave the
                // controller on the previous application's target, and the reverse.
                ApplyProfile(snapshot);

                var controllerApply = StartControllerApply(snapshot, applyCancellation);
                if (controllerApply is null)
                {
                    Log.Info(
                        $"Running-application apply for {snapshot.ApplicationId ?? "(none)"} stopped "
                        + "before the controller target: a newer snapshot is queued or shutdown began.");
                    continue;
                }

                await controllerApply.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeApply, applyCancellation))
                    {
                        _activeApply = null;
                    }
                }

                applyCancellation.Dispose();
            }
        }
    }
}
