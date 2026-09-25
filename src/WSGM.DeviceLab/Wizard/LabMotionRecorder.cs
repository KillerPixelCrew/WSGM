using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Foundation;
using WSGM.DeviceLab.Knowledge;
using static WSGM.DeviceLab.Wizard.LabSensorInterop;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What a motion sensor measures.</summary>
internal enum LabMotionSensorKind
{
    /// <summary>Acceleration, gravity included.</summary>
    Accelerometer,

    /// <summary>Angular velocity.</summary>
    Gyrometer,

    /// <summary>Something else; recorded raw, not analysed.</summary>
    Other
}

/// <summary>One sensor the motion stage found, sampled or not.</summary>
internal sealed record LabMotionSensorInfo
{
    /// <summary>Short ID, for example <c>winrt-accelerometer0</c> or <c>legacy3</c>.</summary>
    public required string Id { get; init; }

    /// <summary><c>winrt</c> or <c>legacy</c>.</summary>
    public required string Source { get; init; }

    /// <summary>What it measures.</summary>
    public required LabMotionSensorKind Kind { get; init; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>Device interface or sensor path; redacted on export.</summary>
    public string? Path { get; init; }

    /// <summary>Legacy sensor type GUID.</summary>
    public Guid? Type { get; init; }

    /// <summary>Legacy sensor category GUID.</summary>
    public Guid? Category { get; init; }

    /// <summary>Legacy sensor state (0 ready).</summary>
    public int? State { get; init; }

    /// <summary>Whether readings were recorded from it.</summary>
    public bool Sampled { get; init; }

    /// <summary>Smallest report interval the sensor accepts, in milliseconds.</summary>
    public double? MinimumIntervalMs { get; init; }

    /// <summary>Report interval before the stage asked for the minimum.</summary>
    public double? OriginalIntervalMs { get; init; }

    /// <summary>Report interval in effect while recording.</summary>
    public double? IntervalMs { get; init; }

    /// <summary>WinRT reading transform, left as the sensor had it.</summary>
    public string? ReadingTransform { get; init; }

    /// <summary>Field names in the order of the recorded values.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Indices into <see cref="Fields" /> of the X, Y and Z axes, when known.</summary>
    public IReadOnlyList<int>? AxisFields { get; init; }

    /// <summary>How the axis fields were chosen: <c>standard</c>, <c>knowledge</c> or <c>custom-guess</c>.</summary>
    public string? AxisSource { get; init; }

    /// <summary>Units the API documents for the axes, when it documents them.</summary>
    public string? Units { get; init; }

    /// <summary>Anything that went wrong opening it, or why it was left closed.</summary>
    public string? Problem { get; init; }

    /// <summary>What the reading depends on, for example a command HC sends that the lab does not.</summary>
    public string? Note { get; init; }
}

/// <summary>One HID input or feature value a sensor collection declares.</summary>
/// <param name="ReportType"><c>input</c> or <c>feature</c>.</param>
/// <param name="UsagePage">Usage page.</param>
/// <param name="Usage">Usage, or the first of a range.</param>
/// <param name="UsageMax">Last usage of a range.</param>
/// <param name="ReportId">Report ID.</param>
/// <param name="BitSize">Bits per value.</param>
/// <param name="ReportCount">Values.</param>
/// <param name="LogicalMin">Logical minimum.</param>
/// <param name="LogicalMax">Logical maximum.</param>
/// <param name="UnitsExp">Unit exponent.</param>
/// <param name="Units">Units.</param>
internal sealed record LabHidValueCap(
    string ReportType,
    int UsagePage,
    int Usage,
    int? UsageMax,
    int ReportId,
    int BitSize,
    int ReportCount,
    int LogicalMin,
    int LogicalMax,
    uint UnitsExp,
    uint Units);

/// <summary>A HID sensor collection (usage page 0x20).</summary>
internal sealed record LabHidSensorCollection
{
    /// <summary>Device interface path; redacted on export.</summary>
    public required string Path { get; init; }

    /// <summary>Vendor ID as four hex digits.</summary>
    public string? VendorId { get; init; }

    /// <summary>Product ID as four hex digits.</summary>
    public string? ProductId { get; init; }

    /// <summary>Top-level usage, for example 0x73 accelerometer 3D or 0x76 gyrometer 3D.</summary>
    public required int Usage { get; init; }

    /// <summary>Input report length.</summary>
    public int InputReportBytes { get; init; }

    /// <summary>Feature report length.</summary>
    public int FeatureReportBytes { get; init; }

    /// <summary>Declared input and feature values.</summary>
    public IReadOnlyList<LabHidValueCap> Values { get; init; } = [];
}

/// <summary>Every motion source the stage found.</summary>
internal sealed record LabMotionInventory
{
    /// <summary>WinRT and legacy sensors.</summary>
    public required IReadOnlyList<LabMotionSensorInfo> Sensors { get; init; }

    /// <summary>HID sensor collections; their readings arrive through the sensor APIs.</summary>
    public required IReadOnlyList<LabHidSensorCollection> HidSensors { get; init; }

    /// <summary>HID collections that could not be opened to read their usage.</summary>
    public int HidUnopened { get; init; }

    /// <summary>Sources that could not be used, with the reason.</summary>
    public required IReadOnlyList<string> Unavailable { get; init; }
}

/// <summary>What one sensor recorded in one step.</summary>
internal sealed record LabMotionSensorStep
{
    /// <summary>Sensor ID from <see cref="LabMotionSensorInfo.Id" />.</summary>
    public required string SensorId { get; init; }

    /// <summary>Readings and statistics.</summary>
    public required LabMotionChannelRecord Record { get; init; }

    /// <summary>Failed polls, for polled sensors.</summary>
    public long ReadFailures { get; init; }

    /// <summary>The last failure, when there was one.</summary>
    public string? LastError { get; init; }

    /// <summary>PROPVARIANT type seen for each field, for legacy sensors.</summary>
    public IReadOnlyList<int>? FieldTypes { get; init; }

    /// <summary>
    ///     How a new reading is told from a repeated one: <c>reading-changed event</c>, <c>input report</c>,
    ///     <c>serial frame</c>, <c>timestamp-or-counter-changed</c>, or <c>unknown; host poll only</c> for a
    ///     legacy sensor with neither a timestamp nor a counter, whose polls are not independent readings.
    /// </summary>
    public string? Freshness { get; init; }
}

/// <summary>Live movement across every sensor, for steps that end when the device is still again.</summary>
/// <param name="CanTell">Whether any gyro or accelerometer is sending data.</param>
/// <param name="Moving">Whether the device is moving now.</param>
/// <param name="Gyro">Largest turn rate away from the reference, in the sensor's units (deg/s for the known APIs).</param>
/// <param name="Acceleration">Largest change of acceleration since the last check.</param>
internal readonly record struct LabMotionMovement(bool CanTell, bool Moving, double Gyro, double Acceleration);

/// <summary>
///     Records every motion sensor Windows offers at once: every WinRT accelerometer and gyrometer
///     instance, and every legacy Sensor API motion, orientation or custom sensor with all of its data
///     fields. HID sensor collections are listed; their readings arrive through the two APIs.
/// </summary>
/// <remarks>
///     WinRT sensors run at their minimum report interval with the reading transform untouched. Legacy
///     sensors are polled every 2 ms on one thread each, because <c>GetData</c> can block; a poll that
///     returns the previous report again is counted, not stored. Report intervals are per-client
///     requests that end with the subscription, and the legacy one is put back on dispose if nothing
///     else changed it. Nothing is filtered: which sensor is the real IMU is decided in
///     <see cref="LabMotionAnalysis" />.
/// </remarks>
internal sealed partial class LabMotionRecorder : IDisposable
{
    private const int MaximumLegacySensors = 64;
    private const int MaximumLegacySampled = 16;
    private const int MaximumLegacyFields = 64;

    // The Claw's physical gyro reports a VT_UI4 hardware report counter as custom field 34; AllyXLab
    // treats field 34 of any format as a counter.
    private const uint HardwareCounterId = 34;

    private static readonly PropertyKey MinimumInterval = new(CommonProperties, 12);
    private static readonly PropertyKey CurrentInterval = new(CommonProperties, 13);
    private static readonly PropertyKey DevicePath = new(CommonProperties, 15);

    private readonly List<(LabMotionSensorInfo Info, LabMotionChannel Channel, IMotionSource? Source)> _channels =
        [];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, double[]> _previous = [];
    private readonly Dictionary<string, double[]> _scratch = [];
    private readonly List<LabHidSensorCollection> _hid = [];
    private readonly List<LabMotionSensorInfo> _listed = [];
    private readonly List<Action> _unsubscribe = [];
    private readonly List<string> _unavailable = [];
    private bool _disposed;
    private int _hidUnopened;

    private LabMotionRecorder()
    {
    }

    /// <summary>Every source found.</summary>
    public LabMotionInventory Inventory => new()
    {
        Sensors = [.. _listed],
        HidSensors = [.. _hid],
        HidUnopened = _hidUnopened,
        Unavailable = [.. _unavailable]
    };

    /// <summary>Sensors that are recorded.</summary>
    public IReadOnlyList<LabMotionSensorInfo> Sampled => [.. _channels.Select(item => item.Info)];

    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Finds and opens every source. Blocking; run it off the UI thread.</summary>
    /// <param name="record">
    ///     The confirmed record: its custom legacy fields are read, and a CH340 it assigns to device
    ///     control is left closed.
    /// </param>
    /// <param name="cancellationToken">Cancels the WinRT lookups.</param>
    /// <returns>The recorder, idle until <see cref="BeginStep" />.</returns>
    public static async Task<LabMotionRecorder> StartAsync(
        DeviceKnowledgeRecord? record,
        CancellationToken cancellationToken)
    {
        LabMotionRecorder recorder = new();
        try
        {
            await recorder.OpenWinRtAsync(cancellationToken);
            recorder.OpenLegacy(record?.Motion);
            recorder.OpenHid();
            recorder.OpenSerial(record);
            return recorder;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    /// <summary>Starts recording a step on every sensor.</summary>
    public void BeginStep()
    {
        var now = Now;
        _previous.Clear();
        foreach (var item in _channels)
        {
            item.Source?.ResetCounters();
            item.Channel.Begin(now);
        }
    }

    /// <summary>Checks whether the device is moving, from the latest reading of every sensor.</summary>
    /// <param name="reference">A still window recorded just before, whose gyro means are the zero.</param>
    /// <returns>The movement now.</returns>
    /// <remarks>Called by the page a few times a second, not per reading.</remarks>
    public LabMotionMovement Movement(IReadOnlyList<LabMotionSensorStep> reference)
    {
        const double turning = 20;
        const double shaking = 0.05;
        var canTell = false;
        double gyro = 0;
        double acceleration = 0;
        foreach (var (info, channel, _) in _channels)
        {
            if (info.AxisFields is not { Count: 3 } axes || info.Kind == LabMotionSensorKind.Other)
            {
                continue;
            }

            if (!_scratch.TryGetValue(info.Id, out var latest))
            {
                _scratch[info.Id] = latest = new double[channel.FieldCount];
            }

            if (!channel.TryLatest(latest) || axes.Any(axis => !double.IsFinite(latest[axis])))
            {
                continue;
            }

            canTell = true;
            var vector = new[] { latest[axes[0]], latest[axes[1]], latest[axes[2]] };
            if (info.Kind == LabMotionSensorKind.Gyrometer)
            {
                var zero = reference.FirstOrDefault(item => item.SensorId == info.Id)?.Record.Fields;
                double sum = 0;
                for (var i = 0; i < 3; i++)
                {
                    var bias = zero is not null && axes[i] < zero.Count ? zero[axes[i]].Mean ?? 0 : 0;
                    sum += (vector[i] - bias) * (vector[i] - bias);
                }

                gyro = Math.Max(gyro, Math.Sqrt(sum));
            }
            else
            {
                if (_previous.TryGetValue(info.Id, out var before))
                {
                    acceleration = Math.Max(acceleration,
                        Math.Sqrt(vector.Zip(before).Sum(pair => (pair.First - pair.Second) * (pair.First - pair.Second))));
                }

                _previous[info.Id] = vector;
            }
        }

        return new LabMotionMovement(canTell, gyro > turning || acceleration > shaking, Math.Round(gyro, 3),
            Math.Round(acceleration, 4));
    }

    /// <summary>Stops recording and returns what every sensor recorded.</summary>
    /// <returns>One entry per sampled sensor.</returns>
    public IReadOnlyList<LabMotionSensorStep> EndStep()
    {
        List<LabMotionSensorStep> steps = [];
        foreach (var (info, channel, source) in _channels)
        {
            steps.Add(new LabMotionSensorStep
            {
                SensorId = info.Id,
                Record = channel.End(),
                ReadFailures = source?.Failures ?? 0,
                LastError = source?.LastError,
                FieldTypes = source?.FieldTypes,
                Freshness = source?.Freshness ?? (info.Source == "winrt" ? "reading-changed event" : null)
            });
        }

        return steps;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var unsubscribe in _unsubscribe)
        {
            try
            {
                unsubscribe();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
            {
                // A sensor that went away cannot be unsubscribed; its object is released with the process.
            }
        }

        _unsubscribe.Clear();
        var sources = _channels.Select(item => item.Source).OfType<IMotionSource>().Distinct().ToList();
        foreach (var source in sources)
        {
            source.RequestStop();
        }

        foreach (var source in sources)
        {
            source.StopAndRelease();
        }

        _channels.Clear();
    }

    private async Task OpenWinRtAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accelerometers = await DeviceInformation
                .FindAllAsync(Accelerometer.GetDeviceSelector(AccelerometerReadingType.Standard))
                .AsTask(cancellationToken);
            foreach (var device in accelerometers)
            {
                var id = $"winrt-accelerometer{_listed.Count(item => item.Kind == LabMotionSensorKind.Accelerometer && item.Source == "winrt")}";
                try
                {
                    var sensor = await Accelerometer.FromIdAsync(device.Id).AsTask(cancellationToken);
                    if (sensor is null)
                    {
                        _listed.Add(WinRtInfo(id, LabMotionSensorKind.Accelerometer, device, "could not be opened"));
                        continue;
                    }

                    OpenAccelerometer(id, device, sensor);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException
                                               or InvalidCastException)
                {
                    _listed.Add(WinRtInfo(id, LabMotionSensorKind.Accelerometer, device, ex.Message));
                }
            }

            var gyrometers = await DeviceInformation.FindAllAsync(Gyrometer.GetDeviceSelector())
                .AsTask(cancellationToken);
            foreach (var device in gyrometers)
            {
                var id = $"winrt-gyrometer{_listed.Count(item => item.Kind == LabMotionSensorKind.Gyrometer && item.Source == "winrt")}";
                try
                {
                    var sensor = await Gyrometer.FromIdAsync(device.Id).AsTask(cancellationToken);
                    if (sensor is null)
                    {
                        _listed.Add(WinRtInfo(id, LabMotionSensorKind.Gyrometer, device, "could not be opened"));
                        continue;
                    }

                    OpenGyrometer(id, device, sensor);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException
                                               or InvalidCastException)
                {
                    _listed.Add(WinRtInfo(id, LabMotionSensorKind.Gyrometer, device, ex.Message));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or TypeLoadException
                                       or InvalidCastException)
        {
            _unavailable.Add($"winrt sensors: {ex.Message}");
        }
    }

    private static LabMotionSensorInfo WinRtInfo(
        string id,
        LabMotionSensorKind kind,
        DeviceInformation device,
        string? problem)
    {
        return new LabMotionSensorInfo
        {
            Id = id,
            Source = "winrt",
            Kind = kind,
            Name = device.Name,
            Path = device.Id,
            Problem = problem
        };
    }

    private void OpenAccelerometer(string id, DeviceInformation device, Accelerometer sensor)
    {
        var original = sensor.ReportInterval;
        var minimum = sensor.MinimumReportInterval;
        if (minimum > 0)
        {
            sensor.ReportInterval = minimum;
        }

        LabMotionChannel channel = new(["x", "y", "z"]);
        var info = WinRtInfo(id, LabMotionSensorKind.Accelerometer, device, null) with
        {
            Sampled = true,
            MinimumIntervalMs = minimum,
            OriginalIntervalMs = original,
            IntervalMs = sensor.ReportInterval,
            ReadingTransform = sensor.ReadingTransform.ToString(),
            Fields = ["x", "y", "z"],
            AxisFields = [0, 1, 2],
            AxisSource = "standard",
            Units = "g"
        };
        TypedEventHandler<Accelerometer, AccelerometerReadingChangedEventArgs> handler = (_, args) =>
        {
            if (!channel.Active)
            {
                return;
            }

            var reading = args.Reading;
            Span<double> values = stackalloc double[3];
            values[0] = reading.AccelerationX;
            values[1] = reading.AccelerationY;
            values[2] = reading.AccelerationZ;
            channel.Add(Now, reading.PerformanceCount?.Ticks ?? reading.Timestamp.UtcTicks, values);
        };
        sensor.ReadingChanged += handler;
        _unsubscribe.Add(() =>
        {
            sensor.ReadingChanged -= handler;
            sensor.ReportInterval = 0;
        });
        _listed.Add(info);
        _channels.Add((info, channel, null));
    }

    private void OpenGyrometer(string id, DeviceInformation device, Gyrometer sensor)
    {
        var original = sensor.ReportInterval;
        var minimum = sensor.MinimumReportInterval;
        if (minimum > 0)
        {
            sensor.ReportInterval = minimum;
        }

        LabMotionChannel channel = new(["x", "y", "z"]);
        var info = WinRtInfo(id, LabMotionSensorKind.Gyrometer, device, null) with
        {
            Sampled = true,
            MinimumIntervalMs = minimum,
            OriginalIntervalMs = original,
            IntervalMs = sensor.ReportInterval,
            ReadingTransform = sensor.ReadingTransform.ToString(),
            Fields = ["x", "y", "z"],
            AxisFields = [0, 1, 2],
            AxisSource = "standard",
            Units = "deg/s"
        };
        TypedEventHandler<Gyrometer, GyrometerReadingChangedEventArgs> handler = (_, args) =>
        {
            if (!channel.Active)
            {
                return;
            }

            var reading = args.Reading;
            Span<double> values = stackalloc double[3];
            values[0] = reading.AngularVelocityX;
            values[1] = reading.AngularVelocityY;
            values[2] = reading.AngularVelocityZ;
            channel.Add(Now, reading.PerformanceCount?.Ticks ?? reading.Timestamp.UtcTicks, values);
        };
        sensor.ReadingChanged += handler;
        _unsubscribe.Add(() =>
        {
            sensor.ReadingChanged -= handler;
            sensor.ReportInterval = 0;
        });
        _listed.Add(info);
        _channels.Add((info, channel, null));
    }

    private void OpenLegacy(DeviceMotionKnowledge? known)
    {
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
                _unavailable.Add($"legacy sensors: none listed (0x{result:X8})");
                return;
            }

            if (collection.GetCount(out var count) < 0)
            {
                _unavailable.Add("legacy sensors: could not be counted");
                return;
            }

            for (uint index = 0; index < Math.Min(count, MaximumLegacySensors); index++)
            {
                ISensor? sensor = null;
                try
                {
                    if (collection.GetAt(index, out sensor) < 0 || sensor is null)
                    {
                        continue;
                    }

                    if (OpenLegacySensor($"legacy{index}", sensor, known))
                    {
                        sensor = null;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
                {
                    _unavailable.Add($"legacy sensor {index}: {ex.Message}");
                }
                finally
                {
                    Release(sensor);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            _unavailable.Add($"legacy sensors: {ex.Message}");
        }
        finally
        {
            Release(collection);
            Release(managerObject);
        }
    }

    // Returns true when the sensor was handed to a poller, which then owns it.
    private bool OpenLegacySensor(string id, ISensor sensor, DeviceMotionKnowledge? known)
    {
        sensor.GetFriendlyName(out var name);
        sensor.GetType(out var type);
        sensor.GetCategory(out var category);
        sensor.GetState(out var state);
        var path = ReadString(sensor, DevicePath);
        List<PropertyKey> keys = [];
        if (sensor.GetSupportedDataFields(out var collection) >= 0 && collection is not null)
        {
            try
            {
                if (collection.GetCount(out var count) >= 0)
                {
                    for (uint i = 0; i < Math.Min(count, MaximumLegacyFields); i++)
                    {
                        PropertyKey key = new();
                        if (collection.GetAt(i, ref key) >= 0)
                        {
                            keys.Add(key);
                        }
                    }
                }
            }
            finally
            {
                Release(collection);
            }
        }

        var fields = keys.Select(key => key.ToString()).ToList();
        var (kind, axes, axisSource, units) = LegacyAxes(name, type, fields, known);
        var interesting = category == CategoryMotion || category == CategoryOrientation
                                                     || type == TypeCustom || kind != LabMotionSensorKind.Other;
        var sampled = interesting && keys.Count > 0
                                  && _channels.Count(item => item.Source is LegacyPoller) < MaximumLegacySampled;
        LabMotionSensorInfo info = new()
        {
            Id = id,
            Source = "legacy",
            Kind = kind,
            Name = name,
            Path = path,
            Type = type,
            Category = category,
            State = state,
            Fields = fields,
            AxisFields = axes,
            AxisSource = axisSource,
            Units = units
        };
        if (!sampled)
        {
            _listed.Add(info);
            return false;
        }

        var minimum = ReadUnsigned(sensor, MinimumInterval);
        var original = ReadUnsigned(sensor, CurrentInterval);
        uint? applied = null;
        string? problem = null;
        if (minimum is > 0 && original != minimum)
        {
            problem = SetUnsigned(sensor, CurrentInterval, minimum.Value);
            if (problem is null)
            {
                applied = minimum;
            }
        }

        var effective = ReadUnsigned(sensor, CurrentInterval);
        info = info with
        {
            Sampled = true,
            MinimumIntervalMs = minimum,
            OriginalIntervalMs = original,
            IntervalMs = effective,
            Problem = problem is null ? null : $"could not request the minimum report interval: {problem}"
        };
        LabMotionChannel channel = new(fields);
        LegacyPoller poller = new(sensor, [.. keys], channel, () => Now, original, applied);
        _listed.Add(info);
        _channels.Add((info, channel, poller));
        poller.Start(id);
        return true;
    }

    /// <summary>Picks the axis fields of a legacy sensor.</summary>
    /// <param name="name">Friendly name.</param>
    /// <param name="type">Sensor type.</param>
    /// <param name="fields">Supported fields as <c>format:pid</c>.</param>
    /// <param name="known">The confirmed record's motion facts.</param>
    /// <returns>Kind, axis field indices, how they were chosen and documented units.</returns>
    internal static (LabMotionSensorKind Kind, IReadOnlyList<int>? Axes, string? Source, string? Units) LegacyAxes(
        string? name,
        Guid type,
        IReadOnlyList<string> fields,
        DeviceMotionKnowledge? known)
    {
        int[]? Find(Guid format, IReadOnlyList<int> ids)
        {
            if (ids.Count != 3)
            {
                return null;
            }

            var found = ids.Select(pid => IndexOf(fields, $"{format:D}:{pid}")).ToArray();
            return found.All(index => index >= 0) ? found : null;
        }

        var nameKind = name?.Contains("gyro", StringComparison.OrdinalIgnoreCase) is true
            ? LabMotionSensorKind.Gyrometer
            : name?.Contains("accel", StringComparison.OrdinalIgnoreCase) is true
                ? LabMotionSensorKind.Accelerometer
                : LabMotionSensorKind.Other;
        foreach (var custom in known?.LegacyFields ?? [])
        {
            if (!string.Equals(custom.FriendlyName, name, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(custom.FormatId, out var format)
                || Find(format, custom.PropertyIds) is not { } axes)
            {
                continue;
            }

            var kind = custom.Kind.Equals("gyrometer", StringComparison.OrdinalIgnoreCase)
                ? LabMotionSensorKind.Gyrometer
                : LabMotionSensorKind.Accelerometer;
            return (kind, axes, "knowledge", null);
        }

        if (Find(MotionData, [2, 3, 4]) is { } acceleration)
        {
            return (LabMotionSensorKind.Accelerometer, acceleration, "standard", "g");
        }

        if (Find(MotionData, [10, 11, 12]) is { } angular)
        {
            return (LabMotionSensorKind.Gyrometer, angular, "standard", "deg/s");
        }

        var typeKind = type == TypeAccelerometer3D
            ? LabMotionSensorKind.Accelerometer
            : type == TypeGyrometer3D
                ? LabMotionSensorKind.Gyrometer
                : nameKind;
        if (typeKind != LabMotionSensorKind.Other && Find(CustomData, [7, 8, 9]) is { } guessed)
        {
            return (typeKind, guessed, "custom-guess", null);
        }

        return (typeKind, null, null, null);
    }

    private static int IndexOf(IReadOnlyList<string> fields, string key)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ReadString(ISensor sensor, PropertyKey key)
    {
        if (sensor.GetProperty(ref key, out var value) < 0)
        {
            return null;
        }

        try
        {
            return value.VariantType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static uint? ReadUnsigned(ISensor sensor, PropertyKey key)
    {
        if (sensor.GetProperty(ref key, out var value) < 0)
        {
            return null;
        }

        try
        {
            return value.VariantType == VtUi4 ? value.UInt32 : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // One request, never retried: an uncertain write is reported, not repeated.
    private static string? SetUnsigned(ISensor sensor, PropertyKey key, uint value)
    {
        IPortableDeviceValues? properties = null;
        IPortableDeviceValues? results = null;
        try
        {
            properties = (IPortableDeviceValues)new PortableDeviceValuesClass();
            var result = properties.SetUnsignedIntegerValue(ref key, value);
            if (result < 0)
            {
                return $"0x{result:X8}";
            }

            result = sensor.SetProperties(properties, out results);
            return result < 0 ? $"0x{result:X8}" : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return ex.Message;
        }
        finally
        {
            Release(results);
            Release(properties);
        }
    }

    internal static unsafe string[] ListInterfaces(Guid guid)
    {
        // The list can grow between the two calls; a second attempt covers a device arriving.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, in guid, 0, 0) != 0 || length < 2)
            {
                return [];
            }

            var buffer = new char[length];
            int code;
            fixed (char* pointer = buffer)
            {
                code = CM_Get_Device_Interface_List(in guid, 0, pointer, length, 0);
            }

            if (code == 0)
            {
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return [];
    }

    // One running source: a legacy poller, a HID reader or a serial reader. A reader that feeds two
    // channels appears twice in the channel list and is stopped once.
    private interface IMotionSource
    {
        long Failures { get; }

        string? LastError { get; }

        IReadOnlyList<int>? FieldTypes { get; }

        string? Freshness { get; }

        void ResetCounters();

        void RequestStop();

        void StopAndRelease();
    }

    // Polls one legacy sensor on its own thread. GetData is synchronous and may wait for a new report,
    // so sharing a thread would let one quiet sensor starve the others.
    private sealed class LegacyPoller : IMotionSource
    {
        private const long PollTicks = -20_000;
        private readonly LabMotionChannel _channel;
        private readonly PropertyKey[] _keys;
        private readonly double[] _last;
        private readonly Func<double> _now;
        private readonly uint? _originalInterval;
        private readonly uint? _appliedInterval;
        private readonly ISensor _sensor;
        private readonly int[] _types;
        private readonly double[] _values;
        private readonly int[] _counters;
        private long _failures;
        private bool _hasLast;
        private volatile bool _sawIdentity;
        private volatile bool _sawNoIdentity;
        private string? _lastMessage;
        private int _lastResult;
        private long _lastTicks = long.MinValue;
        private volatile bool _stop;
        private Thread? _thread;

        public LegacyPoller(
            ISensor sensor,
            PropertyKey[] keys,
            LabMotionChannel channel,
            Func<double> now,
            uint? originalInterval,
            uint? appliedInterval)
        {
            _sensor = sensor;
            _keys = keys;
            _channel = channel;
            _now = now;
            _originalInterval = originalInterval;
            _appliedInterval = appliedInterval;
            _last = new double[keys.Length];
            _types = new int[keys.Length];
            _values = new double[keys.Length];
            _counters = [.. Enumerable.Range(0, keys.Length).Where(i => keys[i].PropertyId == HardwareCounterId)];
        }

        public long Failures => Interlocked.Read(ref _failures);

        public string? Freshness => _sawIdentity
            ? _sawNoIdentity ? "timestamp-or-counter-changed; some polls had neither" : "timestamp-or-counter-changed"
            : _sawNoIdentity
                ? "unknown; host poll only"
                : null;

        public string? LastError => _lastMessage ?? (_lastResult != 0 ? $"GetData returned 0x{_lastResult:X8}" : null);

        public IReadOnlyList<int>? FieldTypes => [.. _types];

        public void Start(string id)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"Device Lab motion {id}" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _failures, 0);
            _lastMessage = null;
            _lastResult = 0;
            _sawIdentity = false;
            _sawNoIdentity = false;
        }

        public void RequestStop()
        {
            _stop = true;
        }

        // Releases the sensor only once its thread has stopped using it; a thread stuck in GetData
        // keeps it, and the process releases it on exit.
        public void StopAndRelease()
        {
            _stop = true;
            if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(2)))
            {
                return;
            }

            try
            {
                RestoreInterval();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                // The interval request ends with the released client anyway.
            }

            Release(_sensor);
        }

        private void RestoreInterval()
        {
            if (_appliedInterval is not { } applied || _originalInterval is not { } original)
            {
                return;
            }

            // Leave a value someone else set since.
            if (ReadUnsigned(_sensor, CurrentInterval) == applied)
            {
                SetUnsigned(_sensor, CurrentInterval, original);
            }
        }

        private void Run()
        {
            SafeWaitHandle? timer = null;
            try
            {
                timer = CreateWaitableTimerEx(0, 0, 0x2, 0x1F0003);
                if (timer.IsInvalid)
                {
                    timer.Dispose();
                    timer = null;
                }
            }
            catch (EntryPointNotFoundException)
            {
                timer = null;
            }

            var due = PollTicks;
            try
            {
                while (!_stop)
                {
                    if (!_channel.Active)
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    Poll();
                    if (timer is not null && SetWaitableTimer(timer, in due, 0, 0, 0, false))
                    {
                        WaitForSingleObject(timer, 100);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                timer?.Dispose();
            }
        }

        private void Poll()
        {
            ISensorDataReport? report = null;
            try
            {
                var result = _sensor.GetData(out report);
                if (result < 0 || report is null)
                {
                    Interlocked.Increment(ref _failures);
                    _lastResult = result;
                    return;
                }

                var ticks = report.GetTimestamp(out var time) >= 0 && time.TryGetTicks(out var stamp)
                    ? stamp
                    : long.MinValue;
                for (var i = 0; i < _keys.Length; i++)
                {
                    var key = _keys[i];
                    if (report.GetSensorValue(ref key, out var value) < 0)
                    {
                        _values[i] = double.NaN;
                        continue;
                    }

                    _values[i] = Numeric(in value);
                    if (_types[i] == 0)
                    {
                        _types[i] = value.VariantType;
                    }

                    PropVariantClear(ref value);
                }

                // AllyXLab's rule: a report is new when its timestamp or its hardware counter moved. A
                // sensor with neither keeps every poll, flagged as host polls rather than readings.
                var identity = ticks != long.MinValue || _counters.Length > 0;
                if (identity)
                {
                    _sawIdentity = true;
                    if (_hasLast && ticks == _lastTicks && SameCounters())
                    {
                        _channel.CountDuplicate();
                        return;
                    }
                }
                else
                {
                    _sawNoIdentity = true;
                }

                _hasLast = true;
                _lastTicks = ticks;
                Array.Copy(_values, _last, _values.Length);
                _channel.Add(_now(), ticks, _values);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                Interlocked.Increment(ref _failures);
                _lastMessage = ex.Message;
            }
            finally
            {
                Release(report);
            }
        }

        private bool SameCounters()
        {
            foreach (var i in _counters)
            {
                if (_values[i] != _last[i] && !(double.IsNaN(_values[i]) && double.IsNaN(_last[i])))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
