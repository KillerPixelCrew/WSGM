using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Device.Sdk.Windows;

/// <summary>Wakes a polling thread at a fixed interval with about a millisecond of accuracy.</summary>
/// <remarks>
///     <c>WaitHandle.WaitOne(8 ms)</c> sleeps for the whole 15.6 ms system tick unless something raised the
///     machine's timer resolution, which halved the ROG Ally pad's 125 Hz poll. A high-resolution waitable
///     timer keeps the rate without raising the resolution for the whole machine, the way HC polls on its
///     8 ms precision timer. Where Windows has no such timer, the ticker falls back to a plain wait.
/// </remarks>
public sealed partial class PrecisionTicker : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly WaitHandle[] _handles;
    private readonly TimeSpan _interval;
    private readonly SafeWaitHandle? _timer;
    private readonly TimerWaitHandle? _timerHandle;

    /// <summary>Starts ticking.</summary>
    /// <param name="interval">Time between ticks, at least one millisecond.</param>
    /// <param name="cancellationToken">Ends every wait as soon as it is cancelled.</param>
    public PrecisionTicker(TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMilliseconds(1));
        _interval = interval;
        var timer = CreateWaitableTimerEx(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        var dueTime = -interval.Ticks;
        if (!timer.IsInvalid
            && SetWaitableTimer(timer, in dueTime, (int)Math.Max(1, interval.TotalMilliseconds), 0, 0, false))
        {
            _timer = timer;
            _timerHandle = new TimerWaitHandle(timer);
            _handles = [_timerHandle, cancellationToken.WaitHandle];
        }
        else
        {
            timer.Dispose();
            _handles = [cancellationToken.WaitHandle];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timerHandle?.Dispose();
        _timer?.Dispose();
    }

    /// <summary>Waits for the next tick.</summary>
    /// <returns>True on a tick; false once the token is cancelled.</returns>
    public bool Wait()
    {
        if (_timer is null)
        {
            return !_handles[0].WaitOne(_interval);
        }

        return WaitHandle.WaitAny(_handles) == 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags,
        uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period,
        nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    private sealed class TimerWaitHandle : WaitHandle
    {
        internal TimerWaitHandle(SafeWaitHandle handle)
        {
            // The ticker owns the handle and closes it; this wrapper only lets WaitAny see it.
            SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), false);
        }
    }
}
