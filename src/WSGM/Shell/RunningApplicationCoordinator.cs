using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Projects the one canonical Steam running-application identity into every per-application
/// consumer: the shared RTSS service and the managed controller target. Rapid transitions coalesce
/// to the latest identity and never retain an executable after Steam reports exit, ambiguity, or
/// loss of observation.
/// </summary>
/// <remarks>
/// One monitor and one projection for both consumers on purpose. A second observer would poll the
/// live Steam client again over CEF and could resolve a different application than the one the RTSS
/// profile was chosen for, so the controller target and the performance profile could disagree about
/// what is running.
/// </remarks>
internal sealed class RunningApplicationCoordinator : IAsyncDisposable
{
    private readonly IRunningApplicationTargetSource _monitor;
    private readonly Func<PerformanceApplicationTarget?, CancellationToken, Task> _setTargetAsync;
    private readonly Func<RunningApplicationTargetSnapshot, CancellationToken, Task>?
        _setControllerTargetAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private IDisposable? _observation;
    private RunningApplicationTargetSnapshot? _pending;
    private Task _worker = Task.CompletedTask;
    private CancellationTokenSource? _activeApply;
    private long _latestGeneration = -1;
    private bool _workerRunning;
    private bool _disposed;

    internal RunningApplicationCoordinator(
        IRunningApplicationTargetSource monitor,
        Func<PerformanceApplicationTarget?, CancellationToken, Task> setTargetAsync,
        Func<RunningApplicationTargetSnapshot, CancellationToken, Task>? setControllerTargetAsync = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _setTargetAsync = setTargetAsync ?? throw new ArgumentNullException(nameof(setTargetAsync));
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
        _shutdown.Cancel();
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
            await _setTargetAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"RTSS running-application target cleanup failed: {ex.Message}");
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ApplyAsync(
        Func<CancellationToken, Task> applyAsync,
        string consumer,
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await applyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"{consumer} running-application target apply failed for generation "
                + $"{snapshot.Generation}: {ex.Message}");
        }
    }

    internal static PerformanceApplicationTarget? Project(
        RunningApplicationTargetSnapshot snapshot)
        => snapshot.State is RunningApplicationTargetState.Active
                or RunningApplicationTargetState.IdentityOnly
            && snapshot.ApplicationId is { Length: > 0 } applicationId
                ? new PerformanceApplicationTarget(
                    applicationId,
                    snapshot.SteamAppId,
                    snapshot.RtssProfileName)
                : null;

    /// <summary>Whether a delivered snapshot predates the newest one already accepted.</summary>
    internal static bool IsOlder(long latestGeneration, RunningApplicationTargetSnapshot snapshot) =>
        snapshot.Generation < latestGeneration;

    private void OnTargetChanged(RunningApplicationTargetSnapshot snapshot) => Queue(snapshot);

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
                _worker = Task.Run(ApplyPendingAsync);
            }
        }

        // Cancel outside the gate because cancellation callbacks are external code. The pending
        // snapshot was installed while holding the gate, so the worker can no longer start an old
        // controller dispatch even if this cancellation races its async continuation.
        TryCancel(activeApply);
    }

    /// <summary>
    /// Starts controller and power reconciliation only while this snapshot is still current.
    /// </summary>
    /// <remarks>
    /// The check and delegate invocation share the queue gate. This gives queueing, disposal, and
    /// controller dispatch one ordering point instead of leaving a race between a separate
    /// supersession check and the call. The delegate's later async work carries the per-snapshot
    /// cancellation token and is retired as soon as a newer snapshot is queued.
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
                ? ApplyAsync(
                    token => applyController(snapshot, token),
                    "Controller",
                    snapshot,
                    applyCancellation.Token)
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
                // Each consumer is applied independently: an RTSS failure must not leave the
                // controller on the previous application's target, and the reverse. A newer queue
                // cancels this RTSS apply so it cannot hold up the authoritative snapshot behind it.
                await ApplyAsync(
                    token => _setTargetAsync(Project(snapshot), token),
                    "RTSS",
                    snapshot,
                    applyCancellation.Token).ConfigureAwait(false);

                Task? controllerApply = StartControllerApply(snapshot, applyCancellation);
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
