using System;
using System.Threading;

namespace WSGM.Input;

/// <summary>What a Steam Deck feedback frame says about who wants motion.</summary>
internal enum MotionSignal
{
    /// <summary>The frame says nothing about motion.</summary>
    None,

    /// <summary>A consumer turned the IMU on: the set-settings report with IMU mode not off.</summary>
    ImuOn,

    /// <summary>The IMU was turned off or every setting was reset.</summary>
    ImuOff,

    /// <summary>
    ///     An SDL Steam Deck driver is holding the pad open. SDL never asks for the IMU ("sensors are
    ///     enabled by default" on a Deck) but feeds a lizard-mode watchdog for as long as the joystick
    ///     is open: a clear-mappings frame and a single-setting write of the right trackpad mode to
    ///     none, every 200 input reports, with no message at close.
    /// </summary>
    ConsumerHeartbeat
}

/// <summary>
///     Folds the two consumer-side motion signals of a Steam Deck target into one answer: the IMU
///     request Steam writes for a layout that uses gyro, and the watchdog heartbeat an SDL application
///     writes while it holds the pad open.
/// </summary>
/// <remarks>
///     Motion is requested while the IMU is on or a heartbeat arrived within
///     <see cref="HeartbeatExpiry" />. SDL feeds the watchdog every 200 reports, about once or twice a
///     second on this bus, and says nothing when it closes the pad, so the heartbeat can only expire.
///     The expiry timer runs only while a heartbeat is fresh.
/// </remarks>
internal sealed class MotionDemandTracker : IDisposable
{
    /// <summary>How long one heartbeat keeps motion requested, several SDL watchdog intervals.</summary>
    internal static readonly TimeSpan HeartbeatExpiry = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _expiry;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private bool _disposed;
    private bool _heartbeatFresh;
    private bool _imuOn;
    private long _lastHeartbeat;
    private bool _requested;

    /// <summary>Creates a tracker.</summary>
    /// <param name="time">The clock, and the source of the expiry timer.</param>
    /// <param name="expiry">How long a heartbeat counts; the default is <see cref="HeartbeatExpiry" />.</param>
    internal MotionDemandTracker(TimeProvider? time = null, TimeSpan? expiry = null)
    {
        _time = time ?? TimeProvider.System;
        _expiry = expiry ?? HeartbeatExpiry;
        _timer = _time.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether a consumer wants motion right now.</summary>
    internal bool Requested
    {
        get
        {
            lock (_gate)
            {
                return _requested;
            }
        }
    }

    /// <summary>Raised when <see cref="Requested" /> changed, outside the tracker's lock.</summary>
    internal event Action<bool>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }

    /// <summary>Applies one signal read out of a feedback frame.</summary>
    /// <param name="signal">The signal.</param>
    internal void Observe(MotionSignal signal)
    {
        bool changed;
        bool requested;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (signal)
            {
                case MotionSignal.ImuOn:
                    _imuOn = true;
                    break;
                case MotionSignal.ImuOff:
                    _imuOn = false;
                    break;
                case MotionSignal.ConsumerHeartbeat:
                    _lastHeartbeat = _time.GetTimestamp();
                    _heartbeatFresh = true;
                    _timer.Change(_expiry, Timeout.InfiniteTimeSpan);
                    break;
                default:
                    return;
            }

            (changed, requested) = RecomputeUnderGate();
        }

        if (changed)
        {
            Changed?.Invoke(requested);
        }
    }

    /// <summary>Forgets every consumer, as a new device has none, and says so if one was counted.</summary>
    internal void Reset()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _imuOn = false;
            _heartbeatFresh = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            (changed, _) = RecomputeUnderGate();
        }

        if (changed)
        {
            Changed?.Invoke(false);
        }
    }

    private void Expire()
    {
        bool changed;
        bool requested;
        lock (_gate)
        {
            if (_disposed || !_heartbeatFresh)
            {
                return;
            }

            var remaining = _expiry - _time.GetElapsedTime(_lastHeartbeat);
            if (remaining > TimeSpan.Zero)
            {
                // A heartbeat landed after the timer was armed; look again when it is due.
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _heartbeatFresh = false;
            (changed, requested) = RecomputeUnderGate();
        }

        if (changed)
        {
            Changed?.Invoke(requested);
        }
    }

    private (bool Changed, bool Requested) RecomputeUnderGate()
    {
        var requested = _imuOn || _heartbeatFresh;
        var changed = requested != _requested;
        _requested = requested;
        return (changed, requested);
    }
}
