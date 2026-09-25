using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using static WSGM.DeviceLab.Wizard.LabSensorInterop;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One sensor from the legacy COM Sensor API, with every field and property it lists.</summary>
internal sealed record LabLegacySensor
{
    /// <summary>Index in the Sensor API's list.</summary>
    public required int Index { get; init; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>Sensor type GUID.</summary>
    public string? Type { get; init; }

    /// <summary>Sensor category GUID.</summary>
    public string? Category { get; init; }

    /// <summary>Sensor state (0 ready, 1 not available, 2 no data, 3 initializing, 4 access denied, 5 error).</summary>
    public int? State { get; init; }

    /// <summary>Every supported data field, as <c>format:pid</c> with a label where known.</summary>
    public IReadOnlyList<LabLegacySensorField> DataFields { get; init; } = [];

    /// <summary>Every property the sensor reports, except its unique ID and serial number.</summary>
    public IReadOnlyList<LabLegacySensorField> Properties { get; init; } = [];

    /// <summary>What could not be read.</summary>
    public string? Problem { get; init; }
}

/// <summary>One legacy Sensor API field or property.</summary>
/// <param name="Key">PROPERTYKEY as <c>format:pid</c>.</param>
/// <param name="Label">Known name, when this tool knows the key.</param>
/// <param name="VariantType">PROPVARIANT type of the value, when a value was read.</param>
/// <param name="Value">The value as text, when it is a number, string, GUID or boolean.</param>
internal sealed record LabLegacySensorField(string Key, string? Label, int? VariantType, string? Value);

internal static partial class LabSystemDump
{
    private const int MaximumLegacySensors = 64;
    private const int MaximumLegacyFields = 128;
    private const ushort VtBool = 11;
    private const ushort VtClsid = 72;

    // SENSOR_PROPERTY_PERSISTENT_UNIQUE_ID and SENSOR_PROPERTY_SERIAL_NUMBER identify the unit.
    private static readonly PropertyKey[] PrivateSensorProperties =
    [
        new(CommonProperties, 5),
        new(CommonProperties, 8)
    ];

    private static readonly Dictionary<string, string> LegacyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [$"{CommonProperties:D}:2"] = "type",
        [$"{CommonProperties:D}:3"] = "state",
        [$"{CommonProperties:D}:6"] = "manufacturer",
        [$"{CommonProperties:D}:7"] = "model",
        [$"{CommonProperties:D}:9"] = "friendly name",
        [$"{CommonProperties:D}:10"] = "description",
        [$"{CommonProperties:D}:11"] = "connection type",
        [$"{CommonProperties:D}:12"] = "minimum report interval",
        [$"{CommonProperties:D}:13"] = "current report interval",
        [$"{CommonProperties:D}:14"] = "change sensitivity",
        [$"{CommonProperties:D}:15"] = "device path",
        [$"{CommonProperties:D}:17"] = "accuracy",
        [$"{CommonProperties:D}:18"] = "resolution",
        [$"{CommonProperties:D}:20"] = "range minimum",
        [$"{CommonProperties:D}:21"] = "range maximum",
        ["db5e0cf2-cf1f-4c18-b46c-d86011d62150:2"] = "timestamp",
        [$"{MotionData:D}:2"] = "acceleration X (g)",
        [$"{MotionData:D}:3"] = "acceleration Y (g)",
        [$"{MotionData:D}:4"] = "acceleration Z (g)",
        [$"{MotionData:D}:10"] = "angular velocity X (deg/s)",
        [$"{MotionData:D}:11"] = "angular velocity Y (deg/s)",
        [$"{MotionData:D}:12"] = "angular velocity Z (deg/s)",
        [$"{CustomData:D}:1"] = "custom usage",
        [$"{CustomData:D}:2"] = "custom boolean array"
    };

    // Lists every sensor the legacy Sensor API has, with all supported data fields and properties.
    // Nothing is set: no report interval, no event sink, no permission request.
    private static List<LabLegacySensor> CollectLegacySensors(LabSystemDumpContext context, List<string> issues)
    {
        List<LabLegacySensor> sensors = [];
        object? managerObject = null;
        ISensorCollection? collection = null;
        try
        {
            managerObject = new SensorManagerClass();
            var manager = (ISensorManager)managerObject;
            var all = CategoryAll;
            var result = manager.GetSensorsByCategory(ref all, out collection);
            if (result < 0 || collection is null)
            {
                AddIssue(issues, $"legacy sensors: none listed (0x{result:X8})");
                return sensors;
            }

            if (collection.GetCount(out var count) < 0)
            {
                AddIssue(issues, "legacy sensors: could not be counted");
                return sensors;
            }

            for (uint index = 0; index < Math.Min(count, MaximumLegacySensors); index++)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                ISensor? sensor = null;
                try
                {
                    if (collection.GetAt(index, out sensor) < 0 || sensor is null)
                    {
                        continue;
                    }

                    sensors.Add(DescribeLegacySensor((int)index, sensor));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    sensors.Add(new LabLegacySensor { Index = (int)index, Problem = ex.Message });
                }
                finally
                {
                    Release(sensor);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AddIssue(issues, $"legacy sensors: {ex.Message}");
        }
        finally
        {
            Release(collection);
            Release(managerObject);
        }

        return sensors;
    }

    private static LabLegacySensor DescribeLegacySensor(int index, ISensor sensor)
    {
        sensor.GetFriendlyName(out var name);
        var hasType = sensor.GetType(out var type) >= 0;
        var hasCategory = sensor.GetCategory(out var category) >= 0;
        var hasState = sensor.GetState(out var state) >= 0;
        return new LabLegacySensor
        {
            Index = index,
            Name = name,
            Type = hasType ? type.ToString("D") : null,
            Category = hasCategory ? category.ToString("D") : null,
            State = hasState ? state : null,
            DataFields = SupportedFields(sensor),
            Properties = AllProperties(sensor)
        };
    }

    private static List<LabLegacySensorField> SupportedFields(ISensor sensor)
    {
        List<LabLegacySensorField> fields = [];
        if (sensor.GetSupportedDataFields(out var keys) < 0 || keys is null)
        {
            return fields;
        }

        // One report, if the sensor has one ready, shows what each field holds. It is a read and changes
        // nothing; a sensor with no data just lists its keys.
        ISensorDataReport? report = null;
        try
        {
            _ = sensor.GetData(out report);
            if (keys.GetCount(out var count) < 0)
            {
                return fields;
            }

            for (uint i = 0; i < Math.Min(count, MaximumLegacyFields); i++)
            {
                PropertyKey key = new();
                if (keys.GetAt(i, ref key) < 0)
                {
                    continue;
                }

                int? variantType = null;
                string? value = null;
                if (report is not null && report.GetSensorValue(ref key, out var raw) >= 0)
                {
                    try
                    {
                        variantType = raw.VariantType;
                        value = VariantText(raw);
                    }
                    finally
                    {
                        PropVariantClear(ref raw);
                    }
                }

                fields.Add(new LabLegacySensorField(key.ToString(), LegacyLabels.GetValueOrDefault(key.ToString()),
                    variantType, value));
            }
        }
        finally
        {
            Release(report);
            Release(keys);
        }

        return fields;
    }

    private static List<LabLegacySensorField> AllProperties(ISensor sensor)
    {
        List<LabLegacySensorField> properties = [];

        // A null key collection asks for every property.
        if (sensor.GetProperties(IntPtr.Zero, out var pointer) < 0 || pointer == IntPtr.Zero)
        {
            return properties;
        }

        object? valuesObject = null;
        try
        {
            valuesObject = Marshal.GetObjectForIUnknown(pointer);
            var values = (IPortableDeviceValues)valuesObject;
            if (values.GetCount(out var count) < 0)
            {
                return properties;
            }

            for (uint i = 0; i < Math.Min(count, MaximumLegacyFields); i++)
            {
                PropertyKey key = new();
                PropVariant value = new();
                if (values.GetAt(i, ref key, ref value) < 0)
                {
                    continue;
                }

                try
                {
                    if (Array.Exists(PrivateSensorProperties,
                            hidden => hidden.FormatId == key.FormatId && hidden.PropertyId == key.PropertyId))
                    {
                        continue;
                    }

                    properties.Add(new LabLegacySensorField(key.ToString(),
                        LegacyLabels.GetValueOrDefault(key.ToString()), value.VariantType, VariantText(value)));
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
        }
        finally
        {
            Release(valuesObject);
            Marshal.Release(pointer);
        }

        return properties;
    }

    private static string? VariantText(in PropVariant value)
    {
        switch (value.VariantType)
        {
            case VtLpwstr:
                return value.Pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(value.Pointer);
            case VtClsid:
                return value.Pointer == IntPtr.Zero ? null : Marshal.PtrToStructure<Guid>(value.Pointer).ToString("D");
            case VtBool:
                return value.Int16 != 0 ? "true" : "false";
            default:
            {
                var number = Numeric(value);
                return double.IsNaN(number) ? null : number.ToString("R", CultureInfo.InvariantCulture);
            }
        }
    }
}
