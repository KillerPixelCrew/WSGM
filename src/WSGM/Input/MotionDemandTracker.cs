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
///     Folds the consumer-side motion signals of a Steam Deck target into one answer: motion is wanted
///     from the first IMU request or SDL watchdog heartbeat until the target is reset.
/// </summary>
/// <remarks>
///     A real Deck switches its IMU on and off in firmware at no cost, so Steam and SDL toggle it
///     freely: Steam writes the IMU mode as layouts and gyro activators change, and SDL's watchdog
///     only arrives while reports flow. Here every toggle opened or closed the Windows sensors, whose
///     restart takes far longer than a firmware switch, and gyro came out choppy and late (Xbox Ally X,
///     3.0.5). So a request latches. A device nobody ever asked for motion still keeps its sensors
///     closed, which is the idle saving the demand exists for.
/// </remarks>
internal sealed class MotionDemandTracker : IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;
    private bool _requested;

    /// <summary>Whether a consumer has asked for motion since the last reset.</summary>
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

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>Raised when <see cref="Requested" /> changed, outside the tracker's lock.</summary>
    internal event Action<bool>? Changed;

    /// <summary>Applies one signal read out of a feedback frame.</summary>
    /// <param name="signal">The signal; only a request changes anything.</param>
    internal void Observe(MotionSignal signal)
    {
        if (signal is not (MotionSignal.ImuOn or MotionSignal.ConsumerHeartbeat))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _requested)
            {
                return;
            }

            _requested = true;
        }

        Changed?.Invoke(true);
    }

    /// <summary>Forgets every consumer, as a new device has none, and says so if one was counted.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            if (_disposed || !_requested)
            {
                return;
            }

            _requested = false;
        }

        Changed?.Invoke(false);
    }
}
