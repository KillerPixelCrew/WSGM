using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>
///     A process-wide clock that advances only while the process can run: Modern Standby, sleep and
///     hibernation add at most <see cref="MaximumStep" /> to it however long they last.
/// </summary>
/// <remarks>
///     Windows documents that the unbiased interrupt time stops for sleep and hibernation, but not
///     whether it stops while a process is frozen in S0 low-power idle, which is how these handhelds
///     sleep. This clock does not depend on that: a dedicated thread observes it every
///     <see cref="Tick" />, so a step much longer than that can only mean nothing in the process ran,
///     and such a step counts as <see cref="MaximumStep" />.
/// </remarks>
public static class ActiveClock
{
    /// <summary>How often the clock is observed.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>The most one observation may add; a longer gap was a freeze.</summary>
    public static readonly TimeSpan MaximumStep = TimeSpan.FromSeconds(1);

    private static readonly Lock Gate = new();
    private static readonly List<(long Ticks, CancellationTokenSource Source)> Pending = [];
    private static readonly AutoResetEvent Wake = new(false);
    private static long _active;
    private static long _lastTimestamp = Stopwatch.GetTimestamp();
    private static int _started;

    /// <summary>The active time since the process first read this clock.</summary>
    public static TimeSpan Now
    {
        get
        {
            EnsureStarted();
            lock (Gate)
            {
                Advance();
                return TimeSpan.FromTicks(_active);
            }
        }
    }

    internal static void CancelAt(long ticks, CancellationTokenSource source)
    {
        EnsureStarted();
        lock (Gate)
        {
            Pending.Add((ticks, source));
        }

        Wake.Set();
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "WSGM active clock", Priority = ThreadPriority.AboveNormal }
            .Start();
    }

    private static void Run()
    {
        List<CancellationTokenSource> due = [];
        var wait = Tick;
        while (true)
        {
            // Never longer than a tick, so a freeze always shows as one long step; shorter when a
            // deadline falls due sooner, so short deadlines cancel on time.
            Wake.WaitOne(wait);
            lock (Gate)
            {
                Advance();
                var now = _active;
                var next = long.MaxValue;
                for (var index = Pending.Count - 1; index >= 0; index--)
                {
                    if (Pending[index].Ticks <= now)
                    {
                        due.Add(Pending[index].Source);
                        Pending.RemoveAt(index);
                    }
                    else
                    {
                        next = Math.Min(next, Pending[index].Ticks);
                    }
                }

                wait = next == long.MaxValue
                    ? Tick
                    : TimeSpan.FromTicks(Math.Clamp(next - now, TimeSpan.TicksPerMillisecond, Tick.Ticks));
            }

            foreach (var source in due)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The operation finished and disposed its source before the deadline passed.
                }
            }

            due.Clear();
        }
    }

    /// <summary>How much of one observed step the clock counts.</summary>
    /// <param name="observed">The monotonic time between two observations.</param>
    /// <returns>The step itself, or <see cref="MaximumStep" /> for a longer one, which was a freeze.</returns>
    public static TimeSpan CountedStep(TimeSpan observed)
    {
        return observed <= TimeSpan.Zero ? TimeSpan.Zero
            : observed > MaximumStep ? MaximumStep
            : observed;
    }

    private static void Advance()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var observed = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
        _lastTimestamp = timestamp;
        _active += CountedStep(observed).Ticks;
    }
}
