using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Everything one motion step recorded, as written to <c>motion-&lt;step&gt;.json</c>.</summary>
internal sealed record LabMotionStepRecord
{
    /// <summary>Step ID from <see cref="LabMotionStep.Id" />.</summary>
    public required string Step { get; init; }

    /// <summary>What the step measures.</summary>
    public required LabMotionStepKind Kind { get; init; }

    /// <summary>When recording started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Planned recording length.</summary>
    public required int Seconds { get; init; }

    /// <summary>Per-sensor readings.</summary>
    public required IReadOnlyList<LabMotionSensorStep> Sensors { get; init; }

    /// <summary>Every input event in the step, raw HID reports from every device included.</summary>
    public LabInputStepRecord? Input { get; init; }

    /// <summary>What the tester said after the step: <c>kept</c>, <c>redo</c> or <c>slipped</c>.</summary>
    public string? Outcome { get; init; }

    /// <summary>Sources that sent data during the countdown just before recording.</summary>
    public IReadOnlyList<LabMotionLiveSource> AliveBefore { get; init; } = [];

    /// <summary>Sources that sent data while recording.</summary>
    public IReadOnlyList<LabMotionLiveSource> Alive { get; init; } = [];

    /// <summary>True when no source sent anything while recording; such a step is flagged, not trusted.</summary>
    public bool Empty { get; init; }

    /// <summary>
    ///     How recording ended: <c>time</c> for a still step (or a rotation no sensor could follow),
    ///     <c>stillness</c> when a rotation stopped, <c>no-movement</c> when none started.
    /// </summary>
    public string EndedBy { get; init; } = "time";

    /// <summary>When the movement started, in milliseconds after recording started.</summary>
    public double? MovementStartedMs { get; init; }

    /// <summary>When the movement was last seen, in milliseconds after recording started.</summary>
    public double? MovementEndedMs { get; init; }
}

/// <summary>A source that sent data in a window.</summary>
/// <param name="Id">Sensor ID, or input device ID for a raw HID device.</param>
/// <param name="Kind"><c>accelerometer</c>, <c>gyrometer</c>, <c>other</c> or <c>hid-reports</c>.</param>
/// <param name="Name">Readable name.</param>
/// <param name="Readings">Readings or reports.</param>
/// <param name="RateHz">Per second: sensor timestamps when there are any, host receipt otherwise.</param>
internal sealed record LabMotionLiveSource(string Id, string Kind, string? Name, long Readings, double? RateHz);

/// <summary>One sensor's bias and noise while lying still.</summary>
/// <param name="SensorId">Sensor ID.</param>
/// <param name="Kind">What it measures.</param>
/// <param name="Axes">Mean and standard deviation per raw axis.</param>
internal sealed record LabMotionStationaryBias(
    string SensorId,
    LabMotionSensorKind Kind,
    IReadOnlyList<LabMotionAxisStats> Axes);

/// <summary>One pose's mean on one raw axis, with how many readings it rests on.</summary>
/// <param name="Pose">Step ID.</param>
/// <param name="Mean">Mean.</param>
/// <param name="Count">Readings; a still sensor that reports only on change gives few, and a few-reading mean is weak.</param>
internal sealed record LabMotionPoseMean(string Pose, double Mean, long Count);

/// <summary>
///     AllyXLab's gravity candidate for one raw accelerometer axis: the poses where it read highest and
///     lowest, the offset between them and half their span (one g in the sensor's units).
/// </summary>
internal sealed record LabMotionGravityAxis
{
    /// <summary>Raw axis.</summary>
    public required string Axis { get; init; }

    /// <summary>Pose with the highest mean.</summary>
    public required string PositivePose { get; init; }

    /// <summary>Pose with the lowest mean.</summary>
    public required string NegativePose { get; init; }

    /// <summary>Midpoint of the two: the axis offset.</summary>
    public required double Offset { get; init; }

    /// <summary>Half the span: one g in the sensor's units.</summary>
    public required double OneGravity { get; init; }

    /// <summary>Whether all six poses were recorded.</summary>
    public required bool CompleteSixPoses { get; init; }

    /// <summary>Every pose's mean.</summary>
    public required IReadOnlyList<LabMotionPoseMean> Poses { get; init; }
}

/// <summary>Mean and noise of one raw axis.</summary>
/// <param name="Axis">Raw axis name.</param>
/// <param name="Mean">Mean; the bias for a gyro at rest.</param>
/// <param name="StdDev">Standard deviation; the noise at rest.</param>
internal sealed record LabMotionAxisStats(string Axis, double Mean, double StdDev);

/// <summary>An accelerometer in one still pose.</summary>
internal sealed record LabMotionPoseResult
{
    /// <summary>Step ID.</summary>
    public required string Step { get; init; }

    /// <summary>Device axis pointing up, for example <c>+Z</c>.</summary>
    public required string Up { get; init; }

    /// <summary>Mean raw X, Y and Z.</summary>
    public required IReadOnlyList<double> Mean { get; init; }

    /// <summary>Length of the mean vector.</summary>
    public required double Magnitude { get; init; }

    /// <summary>Raw axis with the largest mean.</summary>
    public required string DominantAxis { get; init; }

    /// <summary>Sign of that mean.</summary>
    public required int DominantSign { get; init; }

    /// <summary>Whether the readings stayed within 5% of the magnitude.</summary>
    public required bool Steady { get; init; }
}

/// <summary>A gyro during one rotation step.</summary>
internal sealed record LabMotionRotationResult
{
    /// <summary>Step ID.</summary>
    public required string Step { get; init; }

    /// <summary>Device axis turned about.</summary>
    public required string DeviceAxis { get; init; }

    /// <summary>Standard deviation of raw X, Y and Z over the step.</summary>
    public required IReadOnlyList<double> Rms { get; init; }

    /// <summary>Raw axis that moved most.</summary>
    public required string DominantAxis { get; init; }

    /// <summary>Sign of that axis's first large movement.</summary>
    public required int FirstSign { get; init; }

    /// <summary>Largest distance from the step mean on that axis.</summary>
    public required double Peak { get; init; }

    /// <summary>Whether the dominant axis moved at least twice as much as the others and well above rest noise.</summary>
    public required bool Clear { get; init; }
}

/// <summary>A deduced axis compared with the known record's.</summary>
/// <param name="DeviceAxis">Device axis.</param>
/// <param name="DeducedRaw">Raw axis the stage found for it.</param>
/// <param name="DeducedSign">Sign the stage found.</param>
/// <param name="KnownRaw">Raw axis the record gives.</param>
/// <param name="KnownSign">Sign the record gives.</param>
/// <param name="Agrees">Whether both agree; null when either is missing.</param>
internal sealed record LabMotionAxisComparison(
    string DeviceAxis,
    string? DeducedRaw,
    int? DeducedSign,
    string? KnownRaw,
    int? KnownSign,
    bool? Agrees);

/// <summary>What the analysis found for one sensor.</summary>
internal sealed record LabMotionSensorAnalysis
{
    /// <summary>Sensor ID.</summary>
    public required string SensorId { get; init; }

    /// <summary><c>winrt</c> or <c>legacy</c>.</summary>
    public required string Source { get; init; }

    /// <summary>What it measures.</summary>
    public required LabMotionSensorKind Kind { get; init; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>Readings per second by the sensor's timestamps.</summary>
    public double? RateHz { get; init; }

    /// <summary>Readings per second by host receipt.</summary>
    public double? HostRateHz { get; init; }

    /// <summary>Bias and noise per raw axis at rest.</summary>
    public IReadOnlyList<LabMotionAxisStats> Rest { get; init; } = [];

    /// <summary>Length of the rest mean vector; about 1 for an accelerometer in g.</summary>
    public double? RestMagnitude { get; init; }

    /// <summary>What the numbers look like: <c>g</c>, <c>m/s2</c>, <c>deg/s</c>, <c>rad/s</c> or <c>unclear</c>.</summary>
    public string? Units { get; init; }

    /// <summary>Accelerometer poses.</summary>
    public IReadOnlyList<LabMotionPoseResult> Poses { get; init; } = [];

    /// <summary>Per raw axis, the poses it read highest and lowest in.</summary>
    public IReadOnlyList<LabMotionGravityAxis> GravityAxes { get; init; } = [];

    /// <summary>Gyro rotations.</summary>
    public IReadOnlyList<LabMotionRotationResult> Rotations { get; init; } = [];

    /// <summary>For an accelerometer, which gravity sign convention the map assumes and why.</summary>
    public string? Convention { get; init; }

    /// <summary>Deduced raw-to-device map in the device frame of <see cref="LabMotionAnalysis" />.</summary>
    public DeviceAxisMap? Map { get; init; }

    /// <summary>Whether every axis of <see cref="Map" /> was clearly separated.</summary>
    public bool MapClear { get; init; }

    /// <summary><see cref="Map" /> in HC's convention, the shape knowledge records store.</summary>
    public DeviceAxisMap? HcMap { get; init; }

    /// <summary>The known record's map, converted to the same device frame.</summary>
    public DeviceAxisMap? Known { get; init; }

    /// <summary>Per-axis comparison with <see cref="Known" />.</summary>
    public IReadOnlyList<LabMotionAxisComparison> Comparison { get; init; } = [];
}

/// <summary>A controller report offset whose value follows the poses or the turns.</summary>
internal sealed record LabMotionReportCandidate
{
    /// <summary>Input device ID from the capture.</summary>
    public required string Device { get; init; }

    /// <summary>Vendor ID.</summary>
    public string? VendorId { get; init; }

    /// <summary>Product ID.</summary>
    public string? ProductId { get; init; }

    /// <summary>Top-level usage page.</summary>
    public int? UsagePage { get; init; }

    /// <summary>First report byte.</summary>
    public required int ReportId { get; init; }

    /// <summary>Report length.</summary>
    public required int Length { get; init; }

    /// <summary>Byte offset of the little-endian int16.</summary>
    public required int Offset { get; init; }

    /// <summary><c>gravity</c> for pose-correlated, <c>rotation</c> for turn-correlated.</summary>
    public required string Kind { get; init; }

    /// <summary>Separation score: between-pose over within-pose variance, or turn over rest spread.</summary>
    public required double Score { get; init; }

    /// <summary>Device axis it tracks.</summary>
    public string? DeviceAxis { get; init; }

    /// <summary>+1 when the value rises as the positive device axis points up or turns positively.</summary>
    public int? Sign { get; init; }

    /// <summary>Mean value per step.</summary>
    public required IReadOnlyDictionary<string, double> Means { get; init; }

    /// <summary>Reports the candidate rests on.</summary>
    public required int Reports { get; init; }
}

/// <summary>The motion stage's findings, as written to <c>motion-summary.json</c>.</summary>
internal sealed record LabMotionSummary
{
    /// <summary>The device frame every map uses.</summary>
    public string Frame { get; init; } = LabMotionAnalysis.FrameDescription;

    /// <summary>One line for the stage list.</summary>
    public required string Summary { get; init; }

    /// <summary>Steps analysed.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Per-sensor findings, for sensors that sent readings.</summary>
    public required IReadOnlyList<LabMotionSensorAnalysis> Sensors { get; init; }

    /// <summary>Controller report offsets that track the poses or turns.</summary>
    public required IReadOnlyList<LabMotionReportCandidate> Controller { get; init; }

    /// <summary>The knowledge record compared against.</summary>
    public string? KnownRecord { get; init; }

    /// <summary>Motion hazards the record lists.</summary>
    public IReadOnlyList<string> Hazards { get; init; } = [];

    /// <summary>Every way the measurement disagrees with the knowledge record.</summary>
    public IReadOnlyList<string> Disagreements { get; init; } = [];

    /// <summary>Bias and noise per sensor at rest: candidates for a calibrator, not a calibration.</summary>
    public IReadOnlyList<LabMotionStationaryBias> StationaryBias { get; init; } = [];

    /// <summary>What these findings cannot show.</summary>
    public string Limits { get; init; } = LabMotionAnalysis.LimitsDescription;

    /// <summary>What these findings are.</summary>
    public string Status { get; init; } = "Source and measurement candidates only; not an accepted hardware contract.";

    /// <summary>Steps in which no source sent anything.</summary>
    public IReadOnlyList<string> EmptySteps { get; init; } = [];
}

/// <summary>
///     Turns the motion steps into rates, bias, noise, units, axis maps and controller report candidates.
///     Pure: no hardware, no clock.
/// </summary>
/// <remarks>
///     <para>
///         The device frame is right-handed: X points to the right edge, Y up the screen towards the top
///         edge, Z out of the screen towards the viewer. A map uses the <see cref="DeviceAxisMap" /> shape:
///         <c>Swap[raw] = device axis</c>, and <c>device[Swap[raw]] = Sign[device axis] * raw</c>. Rotations
///         are positive counterclockwise by the right-hand rule.
///     </para>
///     <para>
///         Accelerometers are read in the Windows convention unless the paired gyro shows otherwise: a
///         still sensor reports gravity, so the axis pointing up reads -1 g. The gyro map needs no
///         convention, so when one source has both, the accelerometer's signs are checked against the
///         gyro's (one die, one frame).
///     </para>
///     <para>
///         HC's axis maps (<see cref="DeviceMotionKnowledge" />) produce HC's output frame, which is the
///         application frame WSGM and SDL use: X right, Y out of the screen, Z towards the player, that is
///         device (X, Z, -Y). The Claw record, which WSGM's plugin measured, confirms that reading. Known
///         maps are converted to the device frame before comparing.
///     </para>
/// </remarks>
internal static class LabMotionAnalysis
{
    /// <summary>The device frame in words, for the evidence file.</summary>
    public const string FrameDescription =
        "Device frame: X to the right edge, Y up the screen, Z out of the screen. Map: device[Swap[raw]] = Sign[device] * raw. " +
        "Known HC maps are converted from HC's frame (X, Z, -Y).";

    /// <summary>AllyXLab's limits, carried over, plus what the stage adds.</summary>
    public const string LimitsDescription =
        "Sources listed for a step are what reported while it ran, not proof of which device produced the motion. " +
        "A pose with few samples is a weak mean: a still sensor may report only when it changes. " +
        "Stationary means are bias candidates only; the gyro bias can drift, and a steady turn cannot be told from bias by the accelerometer. " +
        "Changed report bytes may be counters, axes or button bits; a correlated offset is a candidate, not a decoding. " +
        "Accelerometer signs assume the Windows convention unless the same device's gyro shows otherwise. " +
        "Rotation signs rest on the tester starting each turn in the direction asked. " +
        "Nothing was sent to any sensor or controller. The stage asks sensors for their fastest report interval and sets the serial IMU port to 115200 baud; both end or are put back when the stage ends.";

    private static readonly string[] Axes = ["X", "Y", "Z"];

    private static readonly int[][] Permutations =
    [
        [0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]
    ];

    /// <summary>Analyses every accepted step.</summary>
    /// <param name="sensors">Every sensor found; only sampled ones are analysed.</param>
    /// <param name="steps">The kept record of each step.</param>
    /// <param name="devices">Input devices the capture saw.</param>
    /// <param name="record">The confirmed knowledge record.</param>
    /// <param name="hidSensorCollections">HID sensor collections found.</param>
    /// <returns>The findings.</returns>
    public static LabMotionSummary Analyze(
        IReadOnlyList<LabMotionSensorInfo> sensors,
        IReadOnlyList<LabMotionStepRecord> steps,
        IReadOnlyList<LabInputDevice> devices,
        DeviceKnowledgeRecord? record,
        int hidSensorCollections)
    {
        var known = record?.Motion;
        List<(LabMotionStep Step, LabMotionStepRecord Record)> kept = [];
        foreach (var step in LabMotionSteps.All)
        {
            if (steps.LastOrDefault(item => item.Step == step.Id) is { } found)
            {
                kept.Add((step, found));
            }
        }

        List<LabMotionSensorAnalysis> analysed = [];
        foreach (var info in sensors.Where(item => item.Sampled))
        {
            List<(LabMotionStep Step, LabMotionChannelRecord Record)> data = [];
            foreach (var (step, stepRecord) in kept)
            {
                if (stepRecord.Sensors.FirstOrDefault(item => item.SensorId == info.Id) is { Record.Count: > 0 } sensor)
                {
                    data.Add((step, sensor.Record));
                }
            }

            if (data.Count == 0)
            {
                continue;
            }

            var knownMap = info.Kind switch
            {
                LabMotionSensorKind.Accelerometer => known?.Accelerometer,
                LabMotionSensorKind.Gyrometer => known?.Gyrometer,
                _ => null
            };
            analysed.Add(AnalyzeSensor(info, data, knownMap));
        }

        analysed =
        [
            .. CheckConvention(analysed).Select(item =>
                item with { HcMap = item.Map is { } map ? ToHcFrame(map) : null })
        ];
        var controller = ReportCandidates(
            [.. kept.Where(item => item.Record.Input is not null).Select(item => (item.Step, item.Record.Input!))],
            devices);
        return new LabMotionSummary
        {
            Summary = Summarize(analysed, controller, known is not null, hidSensorCollections),
            Steps = [.. kept.Select(item => item.Step.Id)],
            EmptySteps = [.. kept.Where(item => item.Record.Empty).Select(item => item.Step.Id)],
            Disagreements = Disagreements(analysed, sensors, known),
            StationaryBias =
            [
                .. analysed.Where(item => item.Rest.Count > 0)
                    .Select(item => new LabMotionStationaryBias(item.SensorId, item.Kind, item.Rest))
            ],
            Sensors = analysed,
            Controller = controller,
            KnownRecord = record?.Id,
            Hazards =
            [
                .. record?.Hazards.Where(hazard =>
                    new[] { "gyro", "accel", "motion", "imu", "axis" }.Any(word =>
                        hazard.Contains(word, StringComparison.OrdinalIgnoreCase))) ?? []
            ]
        };
    }

    /// <summary>A one-line result for the tester after a step.</summary>
    /// <param name="step">The step.</param>
    /// <param name="record">What it recorded.</param>
    /// <param name="sensors">Sampled sensors.</param>
    /// <returns>Plain text.</returns>
    public static string StepResult(
        LabMotionStep step,
        LabMotionStepRecord record,
        IReadOnlyList<LabMotionSensorInfo> sensors)
    {
        var withData = record.Sensors.Count(item => item.Record.Count > 0);
        var devices = record.Input?.Events
            .Where(item => item is { Source: "raw-input", Data: not null, Device: not null })
            .Select(item => item.Device)
            .Distinct()
            .Count() ?? 0;
        if (record.Empty)
        {
            return "Nothing sent any data in this step. Do it again, or continue if this device has no motion sensor.";
        }

        var text = withData > 0 ? $"Recorded {Count(withData, "sensor")}" : "No motion sensor sent readings";
        if (devices > 0)
        {
            text += $" and reports from {Count(devices, "input device")}";
        }

        text += ".";
        var byId = sensors.ToDictionary(item => item.Id);
        if (step.Kind is LabMotionStepKind.Rest or LabMotionStepKind.Pose)
        {
            var moved = record.Sensors.Any(item =>
                byId.TryGetValue(item.SensorId, out var info)
                && info.Kind == LabMotionSensorKind.Accelerometer
                && TryAxes(info, item.Record, out var mean, out var spread)
                && spread.Max() > 0.05 * Magnitude(mean));
            if (moved)
            {
                text += " It may have moved. Do it again if it slipped.";
            }
        }
        else
        {
            var gyros = record.Sensors
                .Where(item => byId.TryGetValue(item.SensorId, out var info)
                               && info.Kind == LabMotionSensorKind.Gyrometer
                               && item.Record.Count > 0)
                .ToList();
            if (record.EndedBy == "no-movement")
            {
                text += " No turning was seen. Do it again.";
            }
            else if (gyros.Count > 0)
            {
                var clear = gyros.Any(item =>
                    Rotation(step, byId[item.SensorId], item.Record, null) is { Clear: true });
                text += clear
                    ? " The turn was measured."
                    : " The turn was small or mixed. Try again with bigger, cleaner movements.";
            }
        }

        return text;
    }

    /// <summary>Analyses one sensor over the steps it recorded.</summary>
    /// <param name="info">The sensor.</param>
    /// <param name="data">Its record in each step, in step order.</param>
    /// <param name="known">The known map in HC's convention, if any.</param>
    /// <returns>The findings.</returns>
    internal static LabMotionSensorAnalysis AnalyzeSensor(
        LabMotionSensorInfo info,
        IReadOnlyList<(LabMotionStep Step, LabMotionChannelRecord Record)> data,
        DeviceAxisMap? known)
    {
        var rateSource = data.FirstOrDefault(item => item.Step.Kind == LabMotionStepKind.Rest).Record
                         ?? data.MaxBy(item => item.Record.Count).Record;
        var analysis = new LabMotionSensorAnalysis
        {
            SensorId = info.Id,
            Source = info.Source,
            Kind = info.Kind,
            Name = info.Name,
            RateHz = rateSource.SensorRateHz,
            HostRateHz = rateSource.HostRateHz
        };
        if (info.AxisFields is not { Count: 3 })
        {
            return analysis;
        }

        double[]? restSpread = null;
        if (data.FirstOrDefault(item => item.Step.Kind == LabMotionStepKind.Rest).Record is { } rest
            && TryAxes(info, rest, out var restMean, out var spread))
        {
            restSpread = spread;
            analysis = analysis with
            {
                Rest = [.. Enumerable.Range(0, 3).Select(i => new LabMotionAxisStats(Axes[i], restMean[i], spread[i]))],
                RestMagnitude = Math.Round(Magnitude(restMean), 6)
            };
        }

        var knownDevice = known is null ? null : ToDeviceFrame(known);
        if (info.Kind == LabMotionSensorKind.Accelerometer)
        {
            analysis = Accelerometer(info, data, analysis);
        }
        else if (info.Kind == LabMotionSensorKind.Gyrometer)
        {
            analysis = Gyrometer(info, data, analysis, restSpread);
        }

        return analysis with
        {
            Known = knownDevice,
            Comparison = analysis.Map is { } map && knownDevice is not null ? Compare(map, knownDevice) : []
        };
    }

    private static LabMotionSensorAnalysis Accelerometer(
        LabMotionSensorInfo info,
        IReadOnlyList<(LabMotionStep Step, LabMotionChannelRecord Record)> data,
        LabMotionSensorAnalysis analysis)
    {
        List<LabMotionPoseResult> poses = [];
        Dictionary<(char Axis, int Sign), double[]> means = [];
        foreach (var (step, record) in data)
        {
            if (step.Kind is not (LabMotionStepKind.Pose or LabMotionStepKind.Rest)
                || !TryAxes(info, record, out var mean, out var spread))
            {
                continue;
            }

            if (step.Kind == LabMotionStepKind.Pose || !means.ContainsKey((step.Axis, step.Sign)))
            {
                means[(step.Axis, step.Sign)] = mean;
            }

            if (step.Kind != LabMotionStepKind.Pose)
            {
                continue;
            }

            var magnitude = Magnitude(mean);
            var dominant = ArgMaxAbs(mean);
            poses.Add(new LabMotionPoseResult
            {
                Step = step.Id,
                Up = $"{(step.Sign > 0 ? '+' : '-')}{step.Axis}",
                Mean = [.. mean.Select(value => Math.Round(value, 6))],
                Magnitude = Math.Round(magnitude, 6),
                DominantAxis = Axes[dominant],
                DominantSign = Math.Sign(mean[dominant]),
                Steady = spread.Max() <= 0.05 * magnitude
            });
        }

        // M[r, a]: raw r's reading with device axis a up, from the up and down poses together.
        var signed = new double[3, 3];
        for (var a = 0; a < 3; a++)
        {
            var up = means.GetValueOrDefault((Axes[a][0], 1));
            var down = means.GetValueOrDefault((Axes[a][0], -1));
            for (var r = 0; r < 3; r++)
            {
                signed[r, a] = up is not null && down is not null
                    ? (up[r] - down[r]) / 2
                    : up is not null
                        ? up[r]
                        : down is not null
                            ? -down[r]
                            : double.NaN;
            }
        }

        List<LabMotionGravityAxis> gravityAxes = [];
        var poseRecords = data.Where(item => item.Step.Kind == LabMotionStepKind.Pose).ToList();
        for (var r = 0; r < 3; r++)
        {
            List<LabMotionPoseMean> axis = [];
            var field = info.AxisFields![r];
            foreach (var (step, record) in poseRecords)
            {
                if (field < record.Fields.Count && record.Fields[field] is { Mean: { } value, Count: > 0 } stats)
                {
                    axis.Add(new LabMotionPoseMean(step.Id, value, stats.Count));
                }
            }

            if (axis.Count < 2)
            {
                continue;
            }

            var high = axis.MaxBy(item => item.Mean)!;
            var low = axis.MinBy(item => item.Mean)!;
            gravityAxes.Add(new LabMotionGravityAxis
            {
                Axis = Axes[r],
                PositivePose = high.Pose,
                NegativePose = low.Pose,
                Offset = Math.Round((high.Mean + low.Mean) / 2, 6),
                OneGravity = Math.Round((high.Mean - low.Mean) / 2, 6),
                CompleteSixPoses = axis.Count == 6,
                Poses = axis
            });
        }

        var magnitudes = means.Values.Select(Magnitude).ToList();
        var gravity = analysis.RestMagnitude ?? (magnitudes.Count > 0 ? magnitudes.Average() : null);
        var (map, clear) = BuildMap(signed, signed, -1, gravity is { } g ? 0.5 * g : 0);
        return analysis with
        {
            Poses = poses,
            GravityAxes = gravityAxes,
            Units = AccelerationUnits(gravity),
            Convention = "gravity (Windows): the axis pointing up reads -1 g; assumed, not checked against a gyro",
            Map = map,
            MapClear = clear
        };
    }

    private static LabMotionSensorAnalysis Gyrometer(
        LabMotionSensorInfo info,
        IReadOnlyList<(LabMotionStep Step, LabMotionChannelRecord Record)> data,
        LabMotionSensorAnalysis analysis,
        double[]? restSpread)
    {
        List<LabMotionRotationResult> rotations = [];
        var strength = new double[3, 3];
        var signs = new double[3, 3];
        for (var r = 0; r < 3; r++)
        {
            for (var a = 0; a < 3; a++)
            {
                strength[r, a] = double.NaN;
                signs[r, a] = double.NaN;
            }
        }

        foreach (var (step, record) in data)
        {
            if (step.Kind != LabMotionStepKind.Rotation
                || Rotation(step, info, record, restSpread) is not { } rotation)
            {
                continue;
            }

            rotations.Add(rotation);
            var a = step.Axis - 'X';
            var largest = rotation.Rms.Max();
            for (var r = 0; r < 3; r++)
            {
                strength[r, a] = largest > 0 ? rotation.Rms[r] / largest : 0;
                signs[r, a] = FirstExcursionSign(Column(info, record, r)) * step.Sign;
            }
        }

        var (map, clear) = BuildMap(strength, signs, 1, 0);
        clear = clear && rotations.Count == 3 && rotations.All(item => item.Clear);
        var peak = rotations.Count > 0 ? rotations.Max(item => item.Peak) : (double?)null;
        return analysis with
        {
            Rotations = rotations,
            Units = RotationUnits(peak),
            Map = map,
            MapClear = clear
        };
    }

    private static LabMotionRotationResult? Rotation(
        LabMotionStep step,
        LabMotionSensorInfo info,
        LabMotionChannelRecord record,
        double[]? restSpread)
    {
        if (!TryAxes(info, record, out _, out var rms))
        {
            return null;
        }

        var dominant = ArgMaxAbs(rms);
        var second = Enumerable.Range(0, 3).Where(i => i != dominant).Max(i => rms[i]);
        var column = Column(info, record, dominant);
        var mean = column.Count > 0 ? column.Average() : 0;
        var peak = column.Count > 0 ? column.Max(value => Math.Abs(value - mean)) : 0;
        var aboveNoise = restSpread is null || rms[dominant] >= 5 * restSpread[dominant];
        return new LabMotionRotationResult
        {
            Step = step.Id,
            DeviceAxis = step.Axis.ToString(),
            Rms = [.. rms.Select(value => Math.Round(value, 6))],
            DominantAxis = Axes[dominant],
            FirstSign = FirstExcursionSign(column),
            Peak = Math.Round(peak, 6),
            Clear = rms[dominant] > 0 && rms[dominant] >= 2 * second && aboveNoise
        };
    }

    /// <summary>
    ///     Picks the raw-to-device assignment with the greatest total strength and builds the map.
    /// </summary>
    /// <param name="strength">Strength of raw axis r (row) for device axis a (column); NaN where unmeasured.</param>
    /// <param name="signed">Signed value whose sign, times <paramref name="signFactor" />, is the map sign.</param>
    /// <param name="signFactor">+1 or -1.</param>
    /// <param name="minimum">Least absolute strength an assignment needs to count as clear.</param>
    /// <returns>The map (only measured axes), and whether every axis was clear.</returns>
    internal static (DeviceAxisMap? Map, bool Clear) BuildMap(
        double[,] strength,
        double[,] signed,
        int signFactor,
        double minimum)
    {
        static double Value(double[,] matrix, int r, int a)
        {
            return double.IsNaN(matrix[r, a]) ? 0 : Math.Abs(matrix[r, a]);
        }

        var best = Permutations.MaxBy(permutation =>
            Enumerable.Range(0, 3).Sum(r => Value(strength, r, permutation[r])))!;
        Dictionary<string, string> swap = [];
        Dictionary<string, int> sign = [];
        var clear = true;
        for (var r = 0; r < 3; r++)
        {
            var a = best[r];
            if (double.IsNaN(strength[r, a]) || double.IsNaN(signed[r, a]) || Math.Sign(signed[r, a]) == 0)
            {
                clear = false;
                continue;
            }

            swap[Axes[r]] = Axes[a];
            sign[Axes[a]] = Math.Sign(signed[r, a]) * signFactor;
            var value = Value(strength, r, a);
            var others = Enumerable.Range(0, 3).Where(i => i != a).Select(i => Value(strength, r, i))
                .Concat(Enumerable.Range(0, 3).Where(i => i != r).Select(i => Value(strength, i, a)));
            if (value < minimum || value < 2 * others.Max())
            {
                clear = false;
            }
        }

        return swap.Count == 0
            ? (null, false)
            : (new DeviceAxisMap { Swap = swap, Sign = sign }, clear && swap.Count == 3);
    }

    /// <summary>Converts an HC map, whose output is HC's frame (X, Z, -Y), to the device frame.</summary>
    /// <param name="hc">HC's map.</param>
    /// <returns>The same map in the device frame.</returns>
    internal static DeviceAxisMap ToDeviceFrame(DeviceAxisMap hc)
    {
        Dictionary<string, string> swap = [];
        Dictionary<string, int> sign = [];
        foreach (var (raw, output) in hc.Swap)
        {
            var hcSign = hc.Sign.GetValueOrDefault(output, 1);
            var (device, factor) = output switch
            {
                "X" => ("X", 1),
                "Y" => ("Z", 1),
                "Z" => ("Y", -1),
                _ => (output, 1)
            };
            swap[raw] = device;
            sign[device] = hcSign * factor;
        }

        return new DeviceAxisMap { Swap = swap, Sign = sign };
    }

    /// <summary>Converts a device-frame map to HC's frame (X, Z, -Y), the inverse of <see cref="ToDeviceFrame" />.</summary>
    /// <param name="device">Map in the device frame.</param>
    /// <returns>The same map as a knowledge record stores it.</returns>
    internal static DeviceAxisMap ToHcFrame(DeviceAxisMap device)
    {
        Dictionary<string, string> swap = [];
        Dictionary<string, int> sign = [];
        foreach (var (raw, axis) in device.Swap)
        {
            var deviceSign = device.Sign.GetValueOrDefault(axis, 1);
            var (output, factor) = axis switch
            {
                "X" => ("X", 1),
                "Y" => ("Z", -1),
                "Z" => ("Y", 1),
                _ => (axis, 1)
            };
            swap[raw] = output;
            sign[output] = deviceSign * factor;
        }

        return new DeviceAxisMap { Swap = swap, Sign = sign };
    }

    /// <summary>Compares two maps in the device frame, axis by axis.</summary>
    /// <param name="deduced">The stage's map.</param>
    /// <param name="known">The record's map.</param>
    /// <returns>One comparison per device axis.</returns>
    internal static IReadOnlyList<LabMotionAxisComparison> Compare(DeviceAxisMap deduced, DeviceAxisMap known)
    {
        static (string? Raw, int? Sign) Source(DeviceAxisMap map, string axis)
        {
            var raw = map.Swap.FirstOrDefault(pair => pair.Value == axis).Key;
            return (raw, raw is not null && map.Sign.TryGetValue(axis, out var value) ? value : null);
        }

        List<LabMotionAxisComparison> comparison = [];
        foreach (var axis in Axes)
        {
            var mine = Source(deduced, axis);
            var theirs = Source(known, axis);
            bool? agrees = mine.Raw is null || theirs.Raw is null || mine.Sign is null || theirs.Sign is null
                ? null
                : mine.Raw == theirs.Raw && mine.Sign == theirs.Sign;
            comparison.Add(new LabMotionAxisComparison(axis, mine.Raw, mine.Sign, theirs.Raw, theirs.Sign, agrees));
        }

        return comparison;
    }

    /// <summary>The sign of the first large movement away from the mean.</summary>
    /// <param name="values">Values in time order.</param>
    /// <returns>+1, -1, or 0 when nothing moved.</returns>
    internal static int FirstExcursionSign(IReadOnlyList<double> values)
    {
        if (values.Count < 3)
        {
            return 0;
        }

        var mean = values.Average();
        var peak = values.Max(value => Math.Abs(value - mean));
        if (peak <= 0)
        {
            return 0;
        }

        foreach (var value in values)
        {
            if (Math.Abs(value - mean) >= 0.5 * peak)
            {
                return Math.Sign(value - mean);
            }
        }

        return 0;
    }

    /// <summary>
    ///     Finds controller report offsets whose little-endian int16 value follows the poses (gravity) or
    ///     the turns (rotation).
    /// </summary>
    /// <param name="steps">Each step with its input record.</param>
    /// <param name="devices">Devices the capture saw.</param>
    /// <returns>Candidates, strongest first; overlapping offsets are dropped in favour of the stronger.</returns>
    internal static IReadOnlyList<LabMotionReportCandidate> ReportCandidates(
        IReadOnlyList<(LabMotionStep Step, LabInputStepRecord Input)> steps,
        IReadOnlyList<LabInputDevice> devices)
    {
        Dictionary<(string Device, int ReportId, int Length), Dictionary<string, List<byte[]>>> groups = [];
        foreach (var (step, input) in steps)
        {
            foreach (var item in input.Events)
            {
                if (item is not { Source: "raw-input", Data: { } hex, Device: { } device } || hex.Length < 8)
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromHexString(hex);
                }
                catch (FormatException)
                {
                    continue;
                }

                var key = (device, (int)bytes[0], bytes.Length);
                if (!groups.TryGetValue(key, out var bySteps))
                {
                    groups[key] = bySteps = [];
                }

                if (!bySteps.TryGetValue(step.Id, out var reports))
                {
                    bySteps[step.Id] = reports = [];
                }

                reports.Add(bytes);
            }
        }

        var byId = devices.ToDictionary(device => device.Id);
        List<LabMotionReportCandidate> gravity = [];
        List<LabMotionReportCandidate> rotation = [];
        foreach (var ((device, reportId, length), bySteps) in groups)
        {
            byId.TryGetValue(device, out var known);

            LabMotionReportCandidate Candidate(int offset, string kind, double score, string? axis, int? sign)
            {
                return new LabMotionReportCandidate
                {
                    Device = device,
                    VendorId = known?.VendorId,
                    ProductId = known?.ProductId,
                    UsagePage = known?.UsagePage,
                    ReportId = reportId,
                    Length = length,
                    Offset = offset,
                    Kind = kind,
                    Score = Math.Round(score, 2),
                    DeviceAxis = axis,
                    Sign = sign,
                    Means = bySteps.ToDictionary(pair => pair.Key,
                        pair => Math.Round(pair.Value.Average(report => Int16(report, offset)), 1)),
                    Reports = bySteps.Values.Sum(reports => reports.Count)
                };
            }

            gravity.AddRange(GravityOffsets(steps, bySteps, length)
                .Select(item => Candidate(item.Offset, "gravity", item.Score, item.Axis, item.Sign)));
            rotation.AddRange(RotationOffsets(steps, bySteps, length)
                .Select(item => Candidate(item.Offset, "rotation", item.Score, item.Axis, item.Sign)));
        }

        return
        [
            .. NonOverlapping(gravity).Take(16),
            .. rotation.GroupBy(item => item.DeviceAxis).SelectMany(group => NonOverlapping(group).Take(4))
        ];
    }

    private static IEnumerable<(int Offset, double Score, string? Axis, int? Sign)> GravityOffsets(
        IReadOnlyList<(LabMotionStep Step, LabInputStepRecord Input)> steps,
        Dictionary<string, List<byte[]>> bySteps,
        int length)
    {
        var poses = steps
            .Where(item => item.Step.Kind == LabMotionStepKind.Pose
                           && bySteps.TryGetValue(item.Step.Id, out var reports) && reports.Count >= 3)
            .Select(item => item.Step)
            .ToList();
        if (poses.Count < 4)
        {
            yield break;
        }

        for (var offset = 1; offset + 1 < length; offset++)
        {
            var stats = poses.Select(pose => MeanAndVariance(bySteps[pose.Id], offset)).ToList();
            var grand = stats.Average(item => item.Mean);
            var between = stats.Average(item => (item.Mean - grand) * (item.Mean - grand));
            var within = stats.Average(item => item.Variance);
            if (between < 4)
            {
                continue;
            }

            var score = between / (within + 1);
            if (score < 25)
            {
                continue;
            }

            string? axis = null;
            int? sign = null;
            var strongest = 0.0;
            foreach (var name in Axes)
            {
                var up = poses.FindIndex(pose => pose.Axis == name[0] && pose.Sign > 0);
                var down = poses.FindIndex(pose => pose.Axis == name[0] && pose.Sign < 0);
                if (up < 0 || down < 0)
                {
                    continue;
                }

                var difference = (stats[up].Mean - stats[down].Mean) / 2;
                if (Math.Abs(difference) > strongest)
                {
                    strongest = Math.Abs(difference);
                    axis = name;
                    sign = Math.Sign(difference);
                }
            }

            yield return (offset, score, axis, sign);
        }
    }

    private static IEnumerable<(int Offset, double Score, string? Axis, int? Sign)> RotationOffsets(
        IReadOnlyList<(LabMotionStep Step, LabInputStepRecord Input)> steps,
        Dictionary<string, List<byte[]>> bySteps,
        int length)
    {
        var baseline = bySteps.TryGetValue(LabMotionSteps.Rest, out var rest) && rest.Count >= 3
            ? rest
            :
            [
                .. steps.Where(item => item.Step.Kind == LabMotionStepKind.Pose)
                    .SelectMany(item => bySteps.GetValueOrDefault(item.Step.Id) ?? [])
            ];
        if (baseline.Count < 3)
        {
            yield break;
        }

        foreach (var (step, _) in steps)
        {
            if (step.Kind != LabMotionStepKind.Rotation || !bySteps.TryGetValue(step.Id, out var reports)
                                                        || reports.Count < 8)
            {
                continue;
            }

            for (var offset = 1; offset + 1 < length; offset++)
            {
                var turning = MeanAndVariance(reports, offset);
                var still = MeanAndVariance(baseline, offset);
                var score = Math.Sqrt(turning.Variance) / (Math.Sqrt(still.Variance) + 1);
                if (score < 8)
                {
                    continue;
                }

                var first = FirstExcursionSign([.. reports.Select(report => (double)Int16(report, offset))]);
                yield return (offset, score, step.Axis.ToString(), first == 0 ? null : first * step.Sign);
            }
        }
    }

    private static IEnumerable<LabMotionReportCandidate> NonOverlapping(IEnumerable<LabMotionReportCandidate> items)
    {
        List<LabMotionReportCandidate> chosen = [];
        foreach (var item in items.OrderByDescending(candidate => candidate.Score))
        {
            if (chosen.Any(other => other.Device == item.Device && other.ReportId == item.ReportId
                                                                && other.Length == item.Length
                                                                && Math.Abs(other.Offset - item.Offset) < 2))
            {
                continue;
            }

            chosen.Add(item);
            yield return item;
        }
    }

    private static (double Mean, double Variance) MeanAndVariance(IReadOnlyList<byte[]> reports, int offset)
    {
        double mean = 0;
        double m2 = 0;
        var n = 0;
        foreach (var report in reports)
        {
            n++;
            double value = Int16(report, offset);
            var delta = value - mean;
            mean += delta / n;
            m2 += delta * (value - mean);
        }

        return (mean, n > 1 ? m2 / (n - 1) : 0);
    }

    private static short Int16(byte[] report, int offset)
    {
        return (short)(report[offset] | (report[offset + 1] << 8));
    }

    /// <summary>Checks each source's accelerometer signs against its gyro, which needs no convention.</summary>
    private static List<LabMotionSensorAnalysis> CheckConvention(List<LabMotionSensorAnalysis> sensors)
    {
        List<LabMotionSensorAnalysis> result = [.. sensors];
        foreach (var source in sensors.Select(PairKey).Distinct())
        {
            var accelerometers = sensors.Where(item => PairKey(item) == source
                                                       && item.Kind == LabMotionSensorKind.Accelerometer
                                                       && item.Map is { Swap.Count: 3 }).ToList();
            var gyrometers = sensors.Where(item => PairKey(item) == source
                                                   && item.Kind == LabMotionSensorKind.Gyrometer
                                                   && item.Map is { Swap.Count: 3 } && item.MapClear).ToList();
            if (accelerometers.Count != 1 || gyrometers.Count != 1)
            {
                continue;
            }

            var accelerometer = accelerometers[0];
            var gyro = gyrometers[0].Map!;
            var map = accelerometer.Map!;
            if (!map.Swap.All(pair => gyro.Swap.GetValueOrDefault(pair.Key) == pair.Value))
            {
                result[result.IndexOf(accelerometer)] = accelerometer with
                {
                    Convention = "gravity (Windows), assumed: the accelerometer's axes do not line up with the gyro's"
                };
                continue;
            }

            var same = Axes.All(axis => map.Sign[axis] == gyro.Sign[axis]);
            var opposite = Axes.All(axis => map.Sign[axis] == -gyro.Sign[axis]);
            LabMotionSensorAnalysis updated;
            if (same)
            {
                updated = accelerometer with
                {
                    Convention = "gravity (Windows): the axis pointing up reads -1 g; agrees with the gyro"
                };
            }
            else if (opposite)
            {
                DeviceAxisMap flipped = new()
                {
                    Swap = map.Swap,
                    Sign = map.Sign.ToDictionary(pair => pair.Key, pair => -pair.Value)
                };
                updated = accelerometer with
                {
                    Convention = "specific force: the axis pointing up reads +1 g; shown by the gyro",
                    Map = flipped,
                    Comparison = accelerometer.Known is { } knownMap ? Compare(flipped, knownMap) : []
                };
            }
            else
            {
                updated = accelerometer with
                {
                    Convention = "gravity (Windows), assumed: the signs match the gyro on some axes only"
                };
            }

            result[result.IndexOf(accelerometer)] = updated;
        }

        return result;
    }

    // Sensors that share one die: a WinRT or legacy pair by API, a controller or serial IMU by device.
    private static string PairKey(LabMotionSensorAnalysis sensor)
    {
        return sensor.Source is "hid" or "serial"
            ? sensor.SensorId.Replace("-accelerometer", string.Empty, StringComparison.Ordinal)
                .Replace("-gyrometer", string.Empty, StringComparison.Ordinal)
            : sensor.Source;
    }

    /// <summary>Lists every way the measurement disagrees with the knowledge record.</summary>
    /// <param name="analysed">Analysed sensors.</param>
    /// <param name="sensors">Every sensor found.</param>
    /// <param name="known">The record's motion facts.</param>
    /// <returns>Plain sentences, one per disagreement.</returns>
    internal static IReadOnlyList<string> Disagreements(
        IReadOnlyList<LabMotionSensorAnalysis> analysed,
        IReadOnlyList<LabMotionSensorInfo> sensors,
        DeviceMotionKnowledge? known)
    {
        List<string> found = [];
        if (known is null)
        {
            return found;
        }

        foreach (var sensor in analysed)
        {
            foreach (var axis in sensor.Comparison.Where(item => item.Agrees == false))
            {
                found.Add(
                    $"{sensor.Name ?? sensor.SensorId} ({sensor.SensorId}): device {axis.DeviceAxis} is raw {axis.DeducedRaw} "
                    + $"times {axis.DeducedSign} here, raw {axis.KnownRaw} times {axis.KnownSign} in the record.");
            }
        }

        foreach (var fields in known.LegacyFields)
        {
            var match = sensors.FirstOrDefault(item => item.Source == "legacy"
                                                       && string.Equals(item.Name, fields.FriendlyName,
                                                           StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                found.Add(
                    $"The record names the legacy sensor \"{fields.FriendlyName}\", which this machine does not list.");
            }
            else if (match.AxisSource != "knowledge")
            {
                found.Add(
                    $"The legacy sensor \"{fields.FriendlyName}\" does not report the fields the record gives ({fields.FormatId} {string.Join("/", fields.PropertyIds)}).");
            }
        }

        if (known.Gyrometer is not null
            && analysed.All(item => item.Kind != LabMotionSensorKind.Gyrometer || item.Map is null))
        {
            found.Add("The record has a gyro axis map, but no gyro here gave one.");
        }

        if (known.Accelerometer is not null
            && analysed.All(item => item.Kind != LabMotionSensorKind.Accelerometer || item.Map is null))
        {
            found.Add("The record has an accelerometer axis map, but no accelerometer here gave one.");
        }

        return found;
    }

    /// <summary>Lists the sources that sent data in a window.</summary>
    /// <param name="sensors">What each sensor recorded.</param>
    /// <param name="input">What the input capture recorded, if it ran.</param>
    /// <param name="infos">Sensors found.</param>
    /// <param name="devices">Input devices the capture saw.</param>
    /// <returns>Live sources, sensors first.</returns>
    public static IReadOnlyList<LabMotionLiveSource> Alive(
        IReadOnlyList<LabMotionSensorStep> sensors,
        LabInputStepRecord? input,
        IReadOnlyList<LabMotionSensorInfo> infos,
        IReadOnlyList<LabInputDevice> devices)
    {
        List<LabMotionLiveSource> alive = [];
        foreach (var sensor in sensors.Where(item => item.Record.Count > 0))
        {
            var info = infos.FirstOrDefault(item => item.Id == sensor.SensorId);
            alive.Add(new LabMotionLiveSource(sensor.SensorId,
                (info?.Kind ?? LabMotionSensorKind.Other).ToString().ToLowerInvariant(), info?.Name,
                sensor.Record.Count, sensor.Record.SensorRateHz ?? sensor.Record.HostRateHz));
        }

        if (input is null)
        {
            return alive;
        }

        var seconds = (input.EndedMs - input.StartedMs) / 1000;
        foreach (var group in input.Events
                     .Where(item => item is { Source: "raw-input", Data: not null, Device: not null })
                     .GroupBy(item => item.Device!))
        {
            var device = devices.FirstOrDefault(item => item.Id == group.Key);
            var name = device is null
                ? group.Key
                : $"{device.VendorId}:{device.ProductId} {device.UsagePage:X4}:{device.Usage:X4}";
            var count = group.Count() + input.Repeated.GetValueOrDefault(group.Key);
            alive.Add(new LabMotionLiveSource(group.Key, "hid-reports", name, count,
                seconds > 0 ? Math.Round(count / seconds, 1) : null));
        }

        return alive;
    }

    /// <summary>Describes live sources in one plain line for the tester.</summary>
    /// <param name="alive">Live sources.</param>
    /// <returns>The line.</returns>
    public static string DescribeAlive(IReadOnlyList<LabMotionLiveSource> alive)
    {
        if (alive.Count == 0)
        {
            return "Nothing is sending motion data right now.";
        }

        var sensors = alive.Where(item => item.Kind != "hid-reports").ToList();
        var devices = alive.Count - sensors.Count;
        List<string> parts = [];
        foreach (var sensor in sensors.Take(6))
        {
            parts.Add(sensor.RateHz is { } rate
                ? $"{sensor.Name ?? sensor.Id} ({rate:0} a second)"
                : sensor.Name ?? sensor.Id);
        }

        if (sensors.Count > 6)
        {
            parts.Add($"{sensors.Count - 6} more");
        }

        if (devices > 0)
        {
            parts.Add($"reports from {Count(devices, "input device")}");
        }

        return "Sending data: " + string.Join(", ", parts) + ".";
    }

    /// <summary>The one-line stage summary.</summary>
    /// <param name="sensors">Analysed sensors.</param>
    /// <param name="controller">Controller candidates.</param>
    /// <param name="knownRecord">Whether a known record with motion facts was confirmed.</param>
    /// <param name="hidSensorCollections">HID sensor collections found.</param>
    /// <returns>The summary.</returns>
    internal static string Summarize(
        IReadOnlyList<LabMotionSensorAnalysis> sensors,
        IReadOnlyList<LabMotionReportCandidate> controller,
        bool knownRecord,
        int hidSensorCollections)
    {
        var fromController = controller.Any(item => item.Kind == "gravity");
        if (sensors.Count == 0 && !fromController)
        {
            return hidSensorCollections > 0
                ? $"No motion readings; Windows lists {Count(hidSensorCollections, "HID sensor")} but none sent data"
                : "No motion sensor found";
        }

        List<string> parts = [sensors.Count > 0 ? Count(sensors.Count, "sensor") : "no Windows motion sensor"];
        var maps = sensors.Where(item => item.Map is not null && item.MapClear).ToList();
        if (sensors.Any(item => item.Map is not null && !item.MapClear))
        {
            parts.Add("ambiguous axis measurements need a repeat");
        }
        if (maps.Count == 0)
        {
            if (sensors.Count > 0)
            {
                parts.Add("axis map unclear");
            }
        }
        else if (knownRecord && maps.Any(item => item.Comparison.Count > 0))
        {
            var differing = maps
                .SelectMany(item => item.Comparison.Where(axis => axis.Agrees == false)
                    .Select(axis => (axis.DeviceAxis, item.Kind)))
                .Distinct()
                .ToList();
            if (differing.Count == 0)
            {
                parts.Add(maps.All(item => item.Comparison.All(axis => axis.Agrees == true))
                    ? "axis map matches the known record"
                    : "axis map partly matches the known record");
            }
            else
            {
                var axes = string.Join(" and ", differing.Select(item => item.DeviceAxis).Distinct().Order());
                var kinds = string.Join(", ", differing.Select(item => item.Kind switch
                {
                    LabMotionSensorKind.Accelerometer => "accelerometer",
                    LabMotionSensorKind.Gyrometer => "gyro",
                    _ => "other"
                }).Distinct().Order());
                parts.Add($"differs from the known record on {axes} ({kinds})");
            }
        }
        else
        {
            parts.Add(maps.Any(item => item.MapClear) ? "axis map found" : "axis map partly found");
        }

        if (fromController)
        {
            parts.Add("controller reports follow the poses");
        }

        return string.Join("; ", parts);
    }

    private static bool TryAxes(
        LabMotionSensorInfo info,
        LabMotionChannelRecord record,
        out double[] mean,
        out double[] spread)
    {
        mean = new double[3];
        spread = new double[3];
        if (info.AxisFields is not { Count: 3 } axes || record.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (axes[i] < 0 || axes[i] >= record.Fields.Count || record.Fields[axes[i]].Mean is not { } value)
            {
                return false;
            }

            mean[i] = value;
            spread[i] = record.Fields[axes[i]].StdDev ?? 0;
        }

        return true;
    }

    private static List<double> Column(LabMotionSensorInfo info, LabMotionChannelRecord record, int axis)
    {
        List<double> values = [];
        if (info.AxisFields is not { Count: 3 } axes)
        {
            return values;
        }

        var index = 2 + axes[axis];
        foreach (var row in record.Samples)
        {
            if (index < row.Length && row[index] is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static int ArgMaxAbs(IReadOnlyList<double> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (Math.Abs(values[i]) > Math.Abs(values[best]))
            {
                best = i;
            }
        }

        return best;
    }

    private static double Magnitude(IReadOnlyList<double> vector)
    {
        return Math.Sqrt(vector.Sum(value => value * value));
    }

    private static string AccelerationUnits(double? magnitude)
    {
        return magnitude switch
        {
            >= 0.85 and <= 1.15 => "g",
            >= 8.3 and <= 11.3 => "m/s2",
            null => "unclear",
            _ => "unclear"
        };
    }

    private static string RotationUnits(double? peak)
    {
        return peak switch
        {
            >= 40 => "deg/s",
            >= 1 and < 15 => "rad/s",
            _ => "unclear"
        };
    }

    private static string Count(int count, string noun)
    {
        return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }
}
