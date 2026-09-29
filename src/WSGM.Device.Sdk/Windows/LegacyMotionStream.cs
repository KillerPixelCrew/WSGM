using System;
using System.Threading;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Sdk.Windows;

/// <summary>Streams an open <see cref="LegacyMotionSensors" /> set: driver events, or a poll where events fail.</summary>
/// <remarks>
///     Readings go straight to the callback on the Sensor API's or the poller's thread, with no queue in
///     between; the callback only stores the latest reading. The poll runs on one thread with a precision
///     ticker, because a plain 2 ms wait sleeps a whole 15.6 ms timer tick. Disposing stops delivery and
///     releases the sensors.
/// </remarks>
public sealed class LegacyMotionStream : IDisposable
{
    /// <summary>
    ///     The poll period, faster than the sensors' 10 ms minimum report interval so scheduler jitter
    ///     cannot routinely skip a report; duplicates are recognised by the report key.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    private readonly CancellationTokenSource? _cancellation;
    private readonly Thread? _poller;
    private readonly LegacyMotionSensors _sensors;
    private bool _disposed;

    private LegacyMotionStream(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading)
    {
        _sensors = sensors;
        if (sensors.TrySubscribe(onReading, out var error))
        {
            return;
        }

        PluginTrace.Info("motion",
            $"IMU events unavailable ({error}); polling every {PollInterval.TotalMilliseconds:F0} ms instead.");
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _poller = new Thread(() => Poll(sensors, onReading, token))
        {
            IsBackground = true,
            Name = "WSGM IMU poll"
        };
        _poller.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _ = _poller!.Join(TimeSpan.FromSeconds(2));
            _cancellation.Dispose();
        }

        // Unsubscribes before the handles go, and returns once no callback is running.
        _sensors.Dispose();
    }

    /// <summary>Starts delivering readings, taking ownership of the sensors.</summary>
    /// <param name="sensors">An open sensor set; the stream disposes it.</param>
    /// <param name="onReading">Takes each fresh reading. It must return quickly and not block.</param>
    /// <returns>The running stream.</returns>
    public static LegacyMotionStream Start(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        ArgumentNullException.ThrowIfNull(onReading);
        return new LegacyMotionStream(sensors, onReading);
    }

    private static void Poll(LegacyMotionSensors sensors, Action<MotionSensorReading> onReading,
        CancellationToken cancellationToken)
    {
        var failing = false;
        using PrecisionTicker ticker = new(PollInterval, cancellationToken);
        while (ticker.Wait())
        {
            var result = sensors.TryRead(out var reading, out var error);
            if (result == MotionSensorReadResult.Failed)
            {
                if (!failing)
                {
                    failing = true;
                    PluginTrace.Warn("motion", $"IMU read failed: {error}");
                }

                continue;
            }

            if (failing)
            {
                failing = false;
                PluginTrace.Info("motion", "IMU readings resumed.");
            }

            if (result == MotionSensorReadResult.Fresh)
            {
                onReading(reading);
            }
        }
    }
}
