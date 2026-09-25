// SPDX-License-Identifier: MIT

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Sensors;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

internal interface IAllyMotionSource : IAsyncDisposable
{
    ValueTask<bool> StartAsync(Func<MotionSample, ValueTask> publish, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>One raw IMU reading in sensor space, before the model's axis map.</summary>
internal readonly record struct AllyImuReading(Vector3 AngularVelocity, Vector3 Acceleration, DateTimeOffset Timestamp);

/// <summary>Builds canonical samples from raw readings with one model's axis maps.</summary>
internal sealed class AllyMotionTransform(AllyModel model)
{
    private readonly StationaryGyroBiasCalibrator _calibrator = new();
    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));

    public Vector3? Bias => _calibrator.Bias;

    public void Reset()
    {
        _calibrator.Reset();
    }

    /// <summary>Applies the axis maps exactly once, then subtracts any measured zero-rate offset.</summary>
    /// <remarks>
    ///     The calibrator runs in the application basis. It needs only the acceleration's magnitude and
    ///     spread, which the axis map preserves, so correcting after the map is equivalent to the
    ///     Claw's sensor-space correction.
    /// </remarks>
    public MotionSample Create(AllyImuReading reading)
    {
        var gyro = _model.Gyro.Apply(reading.AngularVelocity);
        var acceleration = _model.Accelerometer.Apply(reading.Acceleration);
        var corrected = _calibrator.Correct(gyro, acceleration);
        return new MotionSample
        {
            GyroX = corrected.X,
            GyroY = corrected.Y,
            GyroZ = corrected.Z,
            HasGyro = true,
            AccelX = acceleration.X,
            AccelY = acceleration.Y,
            AccelZ = acceleration.Z,
            HasAccelerometer = true,
            SensorTimestamp = reading.Timestamp
        };
    }
}

/// <summary>The Ally IMU through Windows sensors.</summary>
/// <remarks>
///     HC reads the WinRT <c>Gyrometer</c> and <c>Accelerometer</c> first and falls back to the legacy
///     Sensor API only when WinRT exposes neither (<c>IDevice.PullSensors</c>, <c>IDevice.cs:1152-1172</c>;
///     <c>WindowsSensorManager.cs:96-192</c>). The Device Lab run on RC73XA found BMI320 accelerometer and
///     gyrometer sensors on the standard legacy motion fields, which the fallback reads.
/// </remarks>
internal sealed class WindowsAllyMotionSource(AllyModel model) : IAllyMotionSource
{
    private readonly Lock _gate = new();
    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private IDisposable? _session;

    public ValueTask<bool> StartAsync(Func<MotionSample, ValueTask> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_session is not null)
            {
                return ValueTask.FromResult(true);
            }

            _session = WinRtMotionSession.TryStart(_model, publish)
                       ?? (IDisposable?)LegacyMotionSession.TryStart(_model, publish);
            return ValueTask.FromResult(_session is not null);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IDisposable? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }
}

/// <summary>Pumps samples from a producer to the host without blocking the sensor callback.</summary>
internal sealed class MotionPump : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;

    private readonly Channel<MotionSample> _samples = Channel.CreateBounded<MotionSample>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    public MotionPump(Func<MotionSample, ValueTask> publish)
    {
        _pump = PumpAsync(publish, _cancellation.Token);
    }

    public void Dispose()
    {
        _samples.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation of the pump is the expected way it ends.
        }

        _cancellation.Dispose();
    }

    public void Post(MotionSample sample)
    {
        _samples.Writer.TryWrite(sample);
    }

    private async Task PumpAsync(Func<MotionSample, ValueTask> publish, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sample in _samples.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await publish(sample).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure("motion", "Motion publication stopped", ex);
        }
    }
}

/// <summary>WinRT gyrometer and accelerometer at their fastest report interval.</summary>
internal sealed class WinRtMotionSession : IDisposable
{
    private readonly Accelerometer _accelerometer;
    private readonly uint _accelerometerInterval;
    private readonly Lock _gate = new();
    private readonly Gyrometer _gyrometer;
    private readonly uint _gyrometerInterval;
    private readonly MotionPump _pump;
    private readonly AllyMotionTransform _transform;
    private Vector3 _acceleration;
    private bool _disposed;

    private WinRtMotionSession(
        Gyrometer gyrometer,
        Accelerometer accelerometer,
        AllyModel model,
        Func<MotionSample, ValueTask> publish)
    {
        _gyrometer = gyrometer;
        _accelerometer = accelerometer;
        _transform = new AllyMotionTransform(model);
        _pump = new MotionPump(publish);
        _gyrometerInterval = gyrometer.ReportInterval;
        _accelerometerInterval = accelerometer.ReportInterval;
        gyrometer.ReportInterval = Math.Max(gyrometer.MinimumReportInterval, 1u);
        accelerometer.ReportInterval = Math.Max(accelerometer.MinimumReportInterval, 1u);
        if (accelerometer.GetCurrentReading() is { } first)
        {
            _acceleration = new Vector3((float)first.AccelerationX, (float)first.AccelerationY,
                (float)first.AccelerationZ);
        }

        accelerometer.ReadingChanged += OnAcceleration;
        gyrometer.ReadingChanged += OnGyro;
    }

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

        try
        {
            _gyrometer.ReadingChanged -= OnGyro;
            _accelerometer.ReadingChanged -= OnAcceleration;
            // The report interval is shared by every client of the sensor, so the value found at
            // acquisition goes back rather than a fixed default.
            _gyrometer.ReportInterval = _gyrometerInterval;
            _accelerometer.ReportInterval = _accelerometerInterval;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "WinRT sensor release", ex);
        }

        _pump.Dispose();
    }

    public static WinRtMotionSession? TryStart(AllyModel model, Func<MotionSample, ValueTask> publish)
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
                $"WinRT IMU active: gyro {gyrometer.MinimumReportInterval} ms, accel "
                + $"{accelerometer.MinimumReportInterval} ms minimum interval.");
            return new WinRtMotionSession(gyrometer, accelerometer, model, publish);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeLoadException
                                       or UnauthorizedAccessException)
        {
            PluginTrace.Failure("motion", "WinRT sensor discovery failed", ex);
            return null;
        }
    }

    private void OnAcceleration(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        lock (_gate)
        {
            _acceleration = new Vector3((float)reading.AccelerationX, (float)reading.AccelerationY,
                (float)reading.AccelerationZ);
        }
    }

    private void OnGyro(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        var reading = args.Reading;
        MotionSample sample;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            sample = _transform.Create(new AllyImuReading(
                new Vector3((float)reading.AngularVelocityX, (float)reading.AngularVelocityY,
                    (float)reading.AngularVelocityZ),
                _acceleration,
                reading.Timestamp.ToUniversalTime()));
        }

        _pump.Post(sample);
    }
}

/// <summary>The legacy Sensor API with the standard motion fields, polled on one worker.</summary>
internal sealed partial class LegacyMotionSession : IDisposable
{
    private static readonly Guid GyrometerType = new("09485F5A-759E-42C2-BD4B-A349B75C8643");
    private static readonly Guid AccelerometerType = new("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D");
    private static readonly Guid MotionFormat = new("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    private readonly ISensor _accelerometer;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ISensor _gyrometer;
    private readonly Thread _worker;

    private LegacyMotionSession(ISensor gyrometer, ISensor accelerometer, AllyModel model,
        Func<MotionSample, ValueTask> publish)
    {
        _gyrometer = gyrometer;
        _accelerometer = accelerometer;
        var pump = new MotionPump(publish);
        var transform = new AllyMotionTransform(model);
        _worker = new Thread(() => Produce(transform, pump, _cancellation.Token))
        {
            IsBackground = true,
            Name = "WSGM Ally legacy IMU"
        };
        _worker.Start();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _ = _worker.Join(TimeSpan.FromSeconds(2));
        Release(_gyrometer);
        Release(_accelerometer);
        _cancellation.Dispose();
    }

    public static LegacyMotionSession? TryStart(AllyModel model, Func<MotionSample, ValueTask> publish)
    {
        object? manager = null;
        try
        {
            manager = new SensorManagerClass();
            var gyrometer = Find((ISensorManager)manager, GyrometerType, 10);
            var accelerometer = Find((ISensorManager)manager, AccelerometerType, 2);
            if (gyrometer is null || accelerometer is null)
            {
                Release(gyrometer);
                Release(accelerometer);
                PluginTrace.Info("motion", "The legacy Sensor API exposed no standard gyrometer/accelerometer pair.");
                return null;
            }

            PluginTrace.Info("motion", "Legacy Sensor API IMU active on the standard motion fields.");
            return new LegacyMotionSession(gyrometer, accelerometer, model, publish);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            PluginTrace.Failure("motion", "Legacy Sensor API discovery failed", ex);
            return null;
        }
        finally
        {
            Release(manager);
        }
    }

    private static ISensor? Find(ISensorManager manager, Guid type, uint firstField)
    {
        var requested = type;
        if (manager.GetSensorsByType(ref requested, out var collection) < 0 || collection is null)
        {
            return null;
        }

        try
        {
            if (collection.GetCount(out var count) < 0)
            {
                return null;
            }

            for (uint index = 0; index < count; index++)
            {
                if (collection.GetAt(index, out var sensor) < 0 || sensor is null)
                {
                    continue;
                }

                if (sensor.GetState(out var state) >= 0 && state == 0
                                                        && Supports(sensor, firstField)
                                                        && Supports(sensor, firstField + 1)
                                                        && Supports(sensor, firstField + 2))
                {
                    return sensor;
                }

                Release(sensor);
            }

            return null;
        }
        finally
        {
            Release(collection);
        }
    }

    private static bool Supports(ISensor sensor, uint field)
    {
        var key = new PropertyKey(MotionFormat, field);
        return sensor.SupportsDataField(ref key, out var supported) >= 0 && supported != 0;
    }

    private void Produce(AllyMotionTransform transform, MotionPump pump, CancellationToken cancellationToken)
    {
        DateTimeOffset? last = null;
        Vector3 lastGyro = default;
        var failing = false;
        using (pump)
        {
            while (!cancellationToken.WaitHandle.WaitOne(PollInterval))
            {
                if (!TryRead(_gyrometer, 10, out var gyro, out var stamp)
                    || !TryRead(_accelerometer, 2, out var acceleration, out _))
                {
                    if (!failing)
                    {
                        failing = true;
                        PluginTrace.Warn("motion", "Legacy IMU read failed.");
                    }

                    continue;
                }

                if (failing)
                {
                    failing = false;
                    PluginTrace.Info("motion", "Legacy IMU readings resumed.");
                }

                // No report counter exists on the standard fields, so a repeat is an identical
                // timestamp and value; polling runs faster than the sensor reports.
                if (last == stamp && lastGyro == gyro)
                {
                    continue;
                }

                last = stamp;
                lastGyro = gyro;
                pump.Post(transform.Create(new AllyImuReading(gyro, acceleration, stamp)));
            }
        }
    }

    private static bool TryRead(ISensor sensor, uint firstField, out Vector3 value, out DateTimeOffset stamp)
    {
        value = default;
        stamp = default;
        ISensorDataReport? report = null;
        try
        {
            if (sensor.GetData(out report) < 0 || report is null)
            {
                return false;
            }

            if (!TryNumber(report, firstField, out var x) || !TryNumber(report, firstField + 1, out var y)
                                                          || !TryNumber(report, firstField + 2, out var z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            stamp = report.GetTimestamp(out var time) >= 0 && time.TryToUtc(out var utc) ? utc : DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Release(report);
        }
    }

    private static bool TryNumber(ISensorDataReport report, uint field, out float value)
    {
        value = 0;
        var key = new PropertyKey(MotionFormat, field);
        if (report.GetSensorValue(ref key, out var variant) < 0)
        {
            return false;
        }

        try
        {
            double? numeric = variant.VariantType switch
            {
                3 => variant.Int32,
                19 => variant.UInt32,
                4 => variant.Single,
                5 => variant.Double,
                _ => null
            };
            if (numeric is not { } present || !double.IsFinite(present))
            {
                return false;
            }

            value = (float)present;
            return float.IsFinite(value);
        }
        finally
        {
            _ = PropVariantClear(ref variant);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public int Int32;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public float Single;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;

        public readonly bool TryToUtc(out DateTimeOffset timestamp)
        {
            try
            {
                timestamp = new DateTimeOffset(Year, Month, Day, Hour, Minute, Second, Milliseconds, TimeSpan.Zero);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                timestamp = default;
                return false;
            }
        }
    }

    [ComImport]
    [Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorManager
    {
        [PreserveSig]
        int GetSensorsByCategory([In] ref Guid category, out ISensorCollection? sensors);

        [PreserveSig]
        int GetSensorsByType([In] ref Guid type, out ISensorCollection? sensors);
    }

    [ComImport]
    [Guid("23571E11-E545-4DD8-A337-B89BF44B10DF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorCollection
    {
        [PreserveSig]
        int GetAt(uint index, out ISensor? sensor);

        [PreserveSig]
        int GetCount(out uint count);
    }

    [ComImport]
    [Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensor
    {
        [PreserveSig]
        int GetID(out Guid id);

        [PreserveSig]
        int GetCategory(out Guid category);

        [PreserveSig]
        int GetType(out Guid type);

        [PreserveSig]
        int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string? name);

        [PreserveSig]
        int GetProperty([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int GetProperties(nint keys, out nint values);

        [PreserveSig]
        int GetSupportedDataFields(out nint keys);

        [PreserveSig]
        int SetProperties(nint properties, out nint results);

        [PreserveSig]
        int SupportsDataField([In] ref PropertyKey key, out short supported);

        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetData(out ISensorDataReport? report);
    }

    [ComImport]
    [Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorDataReport
    {
        [PreserveSig]
        int GetTimestamp(out SystemTime time);

        [PreserveSig]
        int GetSensorValue([In] ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    private class SensorManagerClass;
}
