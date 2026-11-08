using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Bounded read-only observation started before a desktop launch is dispatched.</summary>
internal sealed class ProgramLaunchObservation : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly ProgramLaunchLineage _lineage;
    private readonly Dictionary<uint, ProcessImageLease> _leases = [];
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;

    internal ProgramLaunchObservation(string executable, CancellationToken lifetime)
    {
        _lineage = new ProgramLaunchLineage(executable, DateTime.UtcNow, ProcessSnapshot.ResolveImagePath(executable));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _worker = Task.Run(ObserveAsync);
    }

    internal Task Ready => _ready.Task;

    internal void Stop()
    {
        Interlocked.Exchange(ref _closed, 1);
        _stop.Cancel();
    }

    internal bool Owns(uint id)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        var identity = ProcessSnapshot.Inspect(id, ProcessLookup.CurrentSessionId);
        lock (_gate)
        {
            return identity is not null && _lineage.Owns(identity);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ObserveAsync()
    {
        ProcessStartWatch? starts = null;
        try
        {
            try
            {
                starts = new ProcessStartWatch(ProcessLookup.CurrentSessionId, start =>
                {
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _closed) == 0)
                        {
                            _lineage.ObserveStart(start);
                        }
                    }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Programs process-start tracing unavailable; using verified snapshots: {ex.Message}");
            }

            // Fast bootstrap processes can exit while scheduled-task cleanup is still returning.
            // Observe concurrently with dispatch, rather than beginning after that handoff.
            var until = DateTime.UtcNow.AddSeconds(30);
            var flushAt = DateTime.UtcNow;
            do
            {
                if (starts is not null && DateTime.UtcNow >= flushAt)
                {
                    starts.Flush();
                    flushAt = DateTime.UtcNow.AddMilliseconds(100);
                }

                var snapshot = ProcessSnapshot.Read();
                lock (_gate)
                {
                    _lineage.Observe(snapshot, Inspect);
                }

                _ready.TrySetResult();
                await Task.Delay(25, _stop.Token).ConfigureAwait(false);
            } while (DateTime.UtcNow < until);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Programs launch ancestry unavailable: {ex.Message}");
        }
        finally
        {
            if (starts is not null)
            {
                try
                {
                    await starts.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"Programs process-start tracing cleanup failed: {ex.Message}");
                }
            }

            lock (_gate)
            {
                foreach (var lease in _leases.Values)
                {
                    lease.Dispose();
                }

                _leases.Clear();
            }

            _ready.TrySetResult();
        }
    }

    private ProcessImageIdentity? Inspect(uint id)
    {
        if (_leases.TryGetValue(id, out var existing))
        {
            return existing.Identity;
        }

        if (ProcessSnapshot.Retain(id, ProcessLookup.CurrentSessionId) is not { } lease)
        {
            return null;
        }

        _leases.Add(id, lease);
        return lease.Identity;
    }
}
