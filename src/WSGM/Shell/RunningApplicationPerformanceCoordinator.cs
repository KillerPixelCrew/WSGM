using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Projects the canonical Steam running-application identity into the shared RTSS service. Rapid
/// transitions coalesce to the latest target and never retain an executable after Steam reports
/// exit, ambiguity, or loss of observation.
/// </summary>
internal sealed class RunningApplicationPerformanceCoordinator : IAsyncDisposable
{
    private readonly RunningApplicationMonitor _monitor;
    private readonly Func<RtssApplicationTarget?, CancellationToken, Task> _setTargetAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private IDisposable? _observation;
    private RunningApplicationTargetSnapshot? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;
    private bool _disposed;

    internal RunningApplicationPerformanceCoordinator(
        RunningApplicationMonitor monitor,
        Func<RtssApplicationTarget?, CancellationToken, Task> setTargetAsync)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _setTargetAsync = setTargetAsync ?? throw new ArgumentNullException(nameof(setTargetAsync));
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            worker = _worker;
        }

        _monitor.Changed -= OnTargetChanged;
        _observation?.Dispose();
        _observation = null;
        _shutdown.Cancel();
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

    internal static RtssApplicationTarget? Project(RunningApplicationTargetSnapshot snapshot)
        => snapshot.State is RunningApplicationTargetState.Active
            && snapshot.ApplicationId is { Length: > 0 } applicationId
            && snapshot.RtssProfileName is { Length: > 0 } profileName
                ? new RtssApplicationTarget(applicationId, profileName)
                : null;

    private void OnTargetChanged(RunningApplicationTargetSnapshot snapshot) => Queue(snapshot);

    private void Queue(RunningApplicationTargetSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = snapshot;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(ApplyPendingAsync);
            }
        }
    }

    private async Task ApplyPendingAsync()
    {
        while (true)
        {
            RunningApplicationTargetSnapshot? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot is null || _disposed)
                {
                    _workerRunning = false;
                    return;
                }
            }

            try
            {
                await _setTargetAsync(Project(snapshot), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"RTSS running-application target apply failed for generation "
                    + $"{snapshot.Generation}: {ex.Message}");
            }
        }
    }
}
