using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Foundation;
using Windows.Graphics.Display;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One Windows sensor as the WinRT sensor API reports it.</summary>
internal sealed record LabDumpSensor
{
    /// <summary>Sensor kind, for example accelerometer.</summary>
    public required string Kind { get; init; }

    /// <summary>Device ID.</summary>
    public required string Id { get; init; }

    /// <summary>Name.</summary>
    public string? Name { get; init; }

    /// <summary>Whether this is the sensor <c>GetDefault</c> returns.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether Windows reports the device as enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Minimum report interval in milliseconds.</summary>
    public uint? MinimumReportInterval { get; init; }

    /// <summary>Largest batch the sensor can deliver.</summary>
    public uint? MaxBatchSize { get; init; }

    /// <summary>Display orientation the readings are transformed to.</summary>
    public string? ReadingTransform { get; init; }

    /// <summary>What could not be read.</summary>
    public string? Problem { get; init; }
}

internal static partial class LabSystemDump
{
    private static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(10);

    private static LabSystemDumpSectionResult CollectSensors(LabSystemDumpContext context)
    {
        List<string> issues = [];
        List<LabDumpSensor> sensors = [];

        // Nothing here sets a report interval or subscribes to readings; each sensor is only opened
        // to read its static properties.
        (string Kind, Func<string> Selector, Func<string?> DefaultId, Func<string, LabDumpSensor> Open)[] kinds =
        [
            ("accelerometer",
                () => Accelerometer.GetDeviceSelector(AccelerometerReadingType.Standard),
                () => Accelerometer.GetDefault()?.DeviceId,
                id => Wait(Accelerometer.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("accelerometer", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, sensor.ReadingTransform)
                    : Missing("accelerometer", id)),
            ("gyrometer",
                Gyrometer.GetDeviceSelector,
                () => Gyrometer.GetDefault()?.DeviceId,
                id => Wait(Gyrometer.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("gyrometer", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, sensor.ReadingTransform)
                    : Missing("gyrometer", id)),
            ("inclinometer",
                () => Inclinometer.GetDeviceSelector(SensorReadingType.Absolute),
                () => Inclinometer.GetDefault()?.DeviceId,
                id => Wait(Inclinometer.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("inclinometer", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, sensor.ReadingTransform)
                    : Missing("inclinometer", id)),
            ("orientation",
                () => OrientationSensor.GetDeviceSelector(SensorReadingType.Absolute),
                () => OrientationSensor.GetDefault()?.DeviceId,
                id => Wait(OrientationSensor.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("orientation", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, sensor.ReadingTransform)
                    : Missing("orientation", id)),
            ("compass",
                Compass.GetDeviceSelector,
                () => Compass.GetDefault()?.DeviceId,
                id => Wait(Compass.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("compass", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, sensor.ReadingTransform)
                    : Missing("compass", id)),
            ("light",
                LightSensor.GetDeviceSelector,
                () => LightSensor.GetDefault()?.DeviceId,
                id => Wait(LightSensor.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("light", id, sensor.MinimumReportInterval, sensor.MaxBatchSize, null)
                    : Missing("light", id)),
            ("simple-orientation",
                SimpleOrientationSensor.GetDeviceSelector,
                () => SimpleOrientationSensor.GetDefault()?.DeviceId,
                id => Wait(SimpleOrientationSensor.FromIdAsync(id), context.Cancellation) is { } sensor
                    ? Sensor("simple-orientation", id, null, null, sensor.ReadingTransform)
                    : Missing("simple-orientation", id))
        ];

        foreach (var (kind, selector, defaultId, open) in kinds)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            try
            {
                var defaultDevice = Guard(defaultId);
                var found = Wait(DeviceInformation.FindAllAsync(selector()), context.Cancellation);
                foreach (var device in found ?? Enumerable.Empty<DeviceInformation>())
                {
                    LabDumpSensor sensor;
                    try
                    {
                        sensor = open(device.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        sensor = Missing(kind, device.Id) with { Problem = ex.Message };
                    }

                    sensors.Add(sensor with
                    {
                        Name = device.Name,
                        Enabled = device.IsEnabled,
                        IsDefault = string.Equals(device.Id, defaultDevice, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                AddIssue(issues, $"{kind}: {ex.Message}");
            }
        }

        // Sensor-class device nodes, from the device tree, catch sensors that WinRT does not list.
        var sensorDevices = context.Devices
            .Where(device => string.Equals(device.Class, "Sensor", StringComparison.OrdinalIgnoreCase))
            .Select(device => new { device.InstanceId, device.FriendlyName, device.Description, device.Service, device.HardwareIds })
            .ToList();
        // The legacy COM Sensor API lists sensors WinRT hides, and every supported field, which is
        // where vendor custom fields show up.
        var legacy = CollectLegacySensors(context, issues);
        context.Write("sensors", new
        {
            Sensors = sensors,
            LegacySensors = legacy,
            SensorClassDevices = sensorDevices,
            Issues = issues
        });
        var count = Math.Max(sensors.Count, legacy.Count);
        return Result("sensors", count,
            $"{Plural(sensors.Count, "Windows sensor", "Windows sensors")}, {Plural(legacy.Count, "legacy sensor", "legacy sensors")}",
            issues);

        static LabDumpSensor Sensor(string kind, string id, uint? interval, uint? batch, DisplayOrientations? transform)
        {
            return new LabDumpSensor
            {
                Kind = kind,
                Id = id,
                MinimumReportInterval = interval,
                MaxBatchSize = batch,
                ReadingTransform = transform?.ToString()
            };
        }

        static LabDumpSensor Missing(string kind, string id)
        {
            return new LabDumpSensor { Kind = kind, Id = id, Problem = "could not be opened" };
        }

        static string? Guard(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }
    }

    private static T? Wait<T>(IAsyncOperation<T> operation, CancellationToken cancellation)
        where T : class
    {
        try
        {
            return operation.AsTask(cancellation).WaitAsync(WinRtTimeout, cancellation).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            operation.Cancel();
            throw new TimeoutException($"Windows did not answer within {WinRtTimeout.TotalSeconds:0} seconds.");
        }
    }
}
