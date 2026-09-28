using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Sensors;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw;

/// <summary>
///     The standard WinRT gyrometer and accelerometer, which HC uses on every Claw whose device JSON
///     declares no <c>WindowsGyrometerFields</c>: ClawA1M and ClawBZ2EM. Neither model has a hardware
///     pass here.
/// </summary>
/// <remarks>
///     HC sets each sensor's report interval to the larger of its minimum and its update interval;
///     10 ms keeps the cadence the gyro bias calibrator's windows were measured at. The intervals found
///     at acquisition go back on dispose, because they are shared by every client of the sensor.
/// </remarks>
internal sealed class WinRtClawMotionSensors : IDisposable
{
    private const uint TargetReportInterval = 10;

    private readonly Accelerometer _accelerometer;
    private readonly uint _accelerometerInterval;
    private readonly Lock _gate = new();
    private readonly Gyrometer _gyrometer;
    private readonly uint _gyrometerInterval;
    private bool _disposed;
    private Vector3? _latestAcceleration;
    private Action<PhysicalMotionReading>? _onReading;

    private WinRtClawMotionSensors(Gyrometer gyrometer, Accelerometer accelerometer)
    {
        _gyrometer = gyrometer;
        _accelerometer = accelerometer;
        _gyrometerInterval = gyrometer.ReportInterval;
        _accelerometerInterval = accelerometer.ReportInterval;
    }

    public void Dispose()
    {
        Unsubscribe();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _gyrometer.ReportInterval = _gyrometerInterval;
            _accelerometer.ReportInterval = _accelerometerInterval;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "WinRT sensor release", ex);
        }
    }

    public static WinRtClawMotionSensors? TryOpen()
    {
        try
        {
            var gyrometer = Gyrometer.GetDefault();
            var accelerometer = Accelerometer.GetDefault();
            if (gyrometer is null || accelerometer is null)
            {
                PluginTrace.Info("motion",
                    $"WinRT sensors: gyrometer={gyrometer is not null}, accelerometer={accelerometer is not null}.");
                return null;
            }

            PluginTrace.Info("motion",
                $"WinRT IMU found: gyro {gyrometer.MinimumReportInterval} ms, accel "
                + $"{accelerometer.MinimumReportInterval} ms minimum interval.");
            return new WinRtClawMotionSensors(gyrometer, accelerometer);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException
                                       or UnauthorizedAccessException)
        {
            PluginTrace.Failure("motion", "WinRT sensor discovery failed", ex);
            return null;
        }
    }

    /// <remarks>
    ///     Gyrometer reports before the first accelerometer report are dropped, as on the physical
    ///     path: the offset calibrator needs the acceleration to recognise rest.
    /// </remarks>
    public bool TrySubscribe(Action<PhysicalMotionReading> onReading, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onReading);
        lock (_gate)
        {
            if (_disposed || _onReading is not null)
            {
                error = _disposed ? "the WinRT IMU is closed" : "the WinRT IMU is already subscribed";
                return false;
            }

            _onReading = onReading;
            _latestAcceleration = null;
        }

        try
        {
            _gyrometer.ReportInterval = Math.Max(_gyrometer.MinimumReportInterval, TargetReportInterval);
            _accelerometer.ReportInterval = Math.Max(_accelerometer.MinimumReportInterval, TargetReportInterval);
            _accelerometer.ReadingChanged += OnAcceleration;
            _gyrometer.ReadingChanged += OnGyro;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Unsubscribe();
            return false;
        }
    }

    /// <summary>Stops event delivery. Returns once no callback is still delivering a reading.</summary>
    public void Unsubscribe()
    {
        try
        {
            _gyrometer.ReadingChanged -= OnGyro;
            _accelerometer.ReadingChanged -= OnAcceleration;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "WinRT sensor unsubscribe", ex);
        }

        // Delivery happens under this lock, so once it is taken no callback is mid-delivery.
        lock (_gate)
        {
            _onReading = null;
        }
    }

    private void OnAcceleration(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            _latestAcceleration = new Vector3(
                (float)reading.AccelerationX,
                (float)reading.AccelerationY,
                (float)reading.AccelerationZ);
        }
    }

    private void OnGyro(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            if (_onReading is not { } onReading || _latestAcceleration is not { } acceleration)
            {
                return;
            }

            onReading(new PhysicalMotionReading(
                new Vector3(
                    (float)reading.AngularVelocityX,
                    (float)reading.AngularVelocityY,
                    (float)reading.AngularVelocityZ),
                acceleration,
                reading.Timestamp.ToUniversalTime()));
        }
    }
}
