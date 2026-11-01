using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The RTSS availability and readback refresh: one shared run at a time, plus the session-long poll.</summary>
/// <remarks>
///     Callers join a refresh already running rather than starting a second one. A kick that arrives
///     mid-run asks for one more run afterwards, so a change noticed during a refresh is never lost
///     and never multiplied. A refresh failure is reported and the poll carries on.
/// </remarks>
internal sealed class PerformanceRefreshLoop
{
    private readonly Action<Exception> _failed;
    private readonly Lock _gate = new();
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly CancellationToken _shutdown;
    private readonly TimeProvider _timeProvider;
    private bool _closed;
    private bool _pending;
    private Task _poll = Task.CompletedTask;
    private bool _running;
    private Task _task = Task.CompletedTask;

    /// <summary>Creates the loop; nothing runs until <see cref="StartPolling" /> or a refresh request.</summary>
    /// <param name="refresh">One refresh run, given the shutdown token.</param>
    /// <param name="failed">Reports a refresh that failed for a reason other than shutdown.</param>
    /// <param name="interval">The poll interval; validated by the owner.</param>
    /// <param name="timeProvider">The clock the poll waits on.</param>
    /// <param name="shutdown">Ends the poll and cancels a running refresh.</param>
    internal PerformanceRefreshLoop(
        Func<CancellationToken, Task> refresh,
        Action<Exception> failed,
        TimeSpan interval,
        TimeProvider timeProvider,
        CancellationToken shutdown)
    {
        _refresh = refresh;
        _failed = failed;
        _interval = interval;
        _timeProvider = timeProvider;
        _shutdown = shutdown;
    }

    /// <summary>Starts the session-long poll.</summary>
    internal void StartPolling()
    {
        lock (_gate)
        {
            _poll = Task.Run(PollAsync);
        }
    }

    /// <summary>Joins or starts the shared refresh.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait, not the shared refresh.</param>
    /// <returns>A task completing when the shared refresh finishes.</returns>
    /// <exception cref="ObjectDisposedException">The loop was closed.</exception>
    internal Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start().WaitAsync(cancellationToken);
    }

    /// <summary>Starts a refresh, or asks the running one to run once more; ignored once closed.</summary>
    internal void Kick()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_running)
            {
                _pending = true;
            }

            Start();
        }
    }

    /// <summary>Closes refresh admission.</summary>
    /// <returns>The poll and the last refresh to wait for, or null when the loop was already closed.</returns>
    internal Task? Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            _closed = true;
            return Task.WhenAll(_poll, _task);
        }
    }

    private Task Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_running)
            {
                _running = true;
                _task = Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            await _refresh(_shutdown).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                        {
                            // Session shutdown closes refresh admission before canceling its work.
                        }
                        catch (Exception ex)
                        {
                            if (!_closed)
                            {
                                _failed(ex);
                            }
                        }

                        lock (_gate)
                        {
                            if (_closed || !_pending)
                            {
                                _running = false;
                                return;
                            }

                            _pending = false;
                        }
                    }
                });
            }

            return _task;
        }
    }

    private async Task PollAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(_shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failed(ex);
            }

            await Task.Delay(_interval, _timeProvider, _shutdown).ConfigureAwait(false);
        }
    }
}
