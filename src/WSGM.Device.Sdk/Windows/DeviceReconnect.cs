using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Device.Sdk.Windows;

/// <summary>Polls every half second for a device that dropped out and reopens it once it is back.</summary>
/// <remarks>
///     A pad or HID collection that disappears is a state, never a device fault: the read loop ends, the
///     service keeps its place in the cycle, and the device is reopened when it reappears. Handheld pads
///     drop off the bus around every sleep (the Xbox Ally X about a second before Windows reports the
///     suspend) and return a few seconds after the wake. The reopen runs once per wait, as HC reopens
///     once per <c>Device_Inserted</c>: an attempt that found the device, or threw, ends the wait, so a
///     write it made is never repeated. What follows a failed reopen is a user action or the next cycle.
/// </remarks>
public sealed class DeviceReconnect
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>The wait whose attempt the current call runs inside, if any.</summary>
    private static readonly AsyncLocal<DeviceReconnect?> Attempting = new();

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;

    /// <summary>
    ///     Runs <paramref name="attempt" /> every half second until it finds the device, throws, or the wait
    ///     is stopped.
    /// </summary>
    /// <param name="attempt">
    ///     One reopen attempt. False only when the device is still absent and nothing was written; true once
    ///     the device was there, whatever came of the reopen.
    /// </param>
    /// <param name="failed">Called with an attempt's exception, which ends the wait.</param>
    /// <remarks>A wait already running is left as it is.</remarks>
    public void Start(Func<CancellationToken, ValueTask<bool>> attempt, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failed);
        lock (_gate)
        {
            if (!_loop.IsCompleted)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _loop = Task.Run(() => RunAsync(attempt, failed, cancellation.Token), CancellationToken.None);
        }
    }

    /// <summary>Stops a running wait and waits for its current attempt to finish.</summary>
    /// <returns>A task completing once no attempt runs.</returns>
    /// <remarks>
    ///     Called from inside an attempt, for example by a release the attempt ran after a failed reopen,
    ///     it only stops the wait: waiting for the attempt from within it would never finish.
    /// </remarks>
    public async ValueTask StopAsync()
    {
        Task loop;
        lock (_gate)
        {
            _cancellation?.Cancel();
            loop = _loop;
        }

        if (ReferenceEquals(Attempting.Value, this))
        {
            return;
        }

        await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_gate)
        {
            if (ReferenceEquals(loop, _loop))
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    private async Task RunAsync(
        Func<CancellationToken, ValueTask<bool>> attempt,
        Action<Exception> failed,
        CancellationToken cancellationToken)
    {
        Attempting.Value = this;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
                if (await attempt(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed(ex);
                return;
            }
        }
    }
}
