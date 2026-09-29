using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Device.Sdk.Windows;

/// <summary>
///     Waits for a device that dropped out to come back and reopens it, the way HC's
///     <c>Device_Removed</c> and <c>Device_Inserted</c> handle it.
/// </summary>
/// <remarks>
///     A pad or HID collection that disappears is a state, never a device fault: the read loop ends, the
///     service keeps its place in the cycle, and the device is reopened when it reappears. Handheld pads
///     drop off the bus around every sleep (the Xbox Ally X about a second before Windows reports the
///     suspend) and return a few seconds after the wake.
/// </remarks>
public sealed class DeviceReconnect
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>The wait whose attempt the current call runs inside, if any.</summary>
    private static readonly AsyncLocal<DeviceReconnect?> Attempting = new();

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;

    /// <summary>Tries <paramref name="attempt" /> every half second until it succeeds or the wait is stopped.</summary>
    /// <param name="attempt">One reopen attempt; true when the device is back.</param>
    /// <param name="failed">Called with an attempt's exception; the wait continues.</param>
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
            }
        }
    }
}
