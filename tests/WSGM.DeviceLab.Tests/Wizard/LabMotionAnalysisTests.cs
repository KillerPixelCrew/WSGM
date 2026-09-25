using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabMotionAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // The synthetic die: raw X points up the screen, raw Y to the left edge, raw Z out of the screen.
    // So device X = -raw Y, device Y = raw X, device Z = raw Z.
    private static readonly DeviceAxisMap Die = new()
    {
        Swap = new Dictionary<string, string> { ["X"] = "Y", ["Y"] = "X", ["Z"] = "Z" },
        Sign = new Dictionary<string, int> { ["X"] = -1, ["Y"] = 1, ["Z"] = 1 }
    };

    private static readonly LabMotionSensorInfo Accelerometer = Sensor("winrt-accelerometer0",
        LabMotionSensorKind.Accelerometer);

    private static readonly LabMotionSensorInfo Gyrometer = Sensor("winrt-gyrometer0", LabMotionSensorKind.Gyrometer);

    [Fact]
    public void Analyze_DeducesTheAxisMapFromPosesAndTurns()
    {
        var summary = LabMotionAnalysis.Analyze([Accelerometer, Gyrometer], Steps(-1), [], null, 0);

        var accelerometer = Assert.Single(summary.Sensors, item => item.Kind == LabMotionSensorKind.Accelerometer);
        var gyro = Assert.Single(summary.Sensors, item => item.Kind == LabMotionSensorKind.Gyrometer);
        AssertMap(Die, accelerometer.Map);
        AssertMap(Die, gyro.Map);
        Assert.True(accelerometer.MapClear);
        Assert.True(gyro.MapClear);
        Assert.StartsWith("gravity (Windows)", accelerometer.Convention, StringComparison.Ordinal);
        Assert.Contains("agrees with the gyro", accelerometer.Convention, StringComparison.Ordinal);
        Assert.Equal("g", accelerometer.Units);
        Assert.Equal("deg/s", gyro.Units);
        Assert.Equal("2 sensors; axis map found", summary.Summary);
    }

    [Fact]
    public void Analyze_FlipsASpecificForceAccelerometerToMatchItsGyro()
    {
        var summary = LabMotionAnalysis.Analyze([Accelerometer, Gyrometer], Steps(1), [], null, 0);

        var accelerometer = Assert.Single(summary.Sensors, item => item.Kind == LabMotionSensorKind.Accelerometer);
        AssertMap(Die, accelerometer.Map);
        Assert.StartsWith("specific force", accelerometer.Convention, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ReportsGravityCandidatesPerRawAxis()
    {
        var summary = LabMotionAnalysis.Analyze([Accelerometer], Steps(-1), [], null, 0);

        var accelerometer = Assert.Single(summary.Sensors);
        var rawX = Assert.Single(accelerometer.GravityAxes, axis => axis.Axis == "X");
        // Raw X is device Y; in the Windows convention it reads -1 when device Y points up.
        Assert.Equal("upside-down", rawX.PositivePose);
        Assert.Equal("upright", rawX.NegativePose);
        Assert.Equal(1, rawX.OneGravity, 6);
        Assert.Equal(0, rawX.Offset, 6);
        Assert.True(rawX.CompleteSixPoses);
    }

    [Fact]
    public void Analyze_ComparesWithTheKnownRecordAndListsDisagreements()
    {
        // HC frame (X, Z, -Y): the die above, written the way HC writes it.
        DeviceAxisMap hc = new()
        {
            Swap = new Dictionary<string, string> { ["X"] = "Z", ["Y"] = "X", ["Z"] = "Y" },
            Sign = new Dictionary<string, int> { ["Z"] = -1, ["X"] = -1, ["Y"] = 1 }
        };
        var matching = Record(new DeviceMotionKnowledge { Accelerometer = hc, Gyrometer = hc });
        var summary = LabMotionAnalysis.Analyze([Accelerometer, Gyrometer], Steps(-1), [], matching, 0);
        Assert.Equal("2 sensors; axis map matches the known record", summary.Summary);
        Assert.Empty(summary.Disagreements);

        DeviceAxisMap flippedZ = new()
            { Swap = hc.Swap, Sign = new Dictionary<string, int> { ["Z"] = -1, ["X"] = -1, ["Y"] = -1 } };
        var differing = Record(new DeviceMotionKnowledge { Accelerometer = hc, Gyrometer = flippedZ });
        summary = LabMotionAnalysis.Analyze([Accelerometer, Gyrometer], Steps(-1), [], differing, 0);
        Assert.Equal("2 sensors; differs from the known record on Z (gyro)", summary.Summary);
        Assert.Single(summary.Disagreements);
    }

    [Fact]
    public void Analyze_ListsALegacySensorTheRecordNamesButTheMachineLacks()
    {
        var record = Record(new DeviceMotionKnowledge
        {
            LegacyFields =
            [
                new DeviceLegacySensorFields
                {
                    Kind = "gyrometer",
                    FriendlyName = "Physical Gyrometer",
                    FormatId = "b14c764f-07cf-41e8-9d82-ebe3d0776a6f",
                    PropertyIds = [7, 8, 9]
                }
            ]
        });

        var summary = LabMotionAnalysis.Analyze([Accelerometer], Steps(-1), [], record, 0);

        Assert.Contains(summary.Disagreements, line => line.Contains("Physical Gyrometer", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_SaysPlainlyWhenNothingRecorded()
    {
        List<LabMotionStepRecord> steps =
        [
            .. LabMotionSteps.All.Select(step => new LabMotionStepRecord
            {
                Step = step.Id, Kind = step.Kind, StartedAt = Now, Seconds = step.Seconds, Sensors = [], Empty = true
            })
        ];

        var summary = LabMotionAnalysis.Analyze([], steps, [], null, 0);

        Assert.Equal("No motion sensor found", summary.Summary);
        Assert.Equal(LabMotionSteps.All.Count, summary.EmptySteps.Count);
    }

    [Fact]
    public void ToDeviceFrame_TurnsTheClawRecordIntoTheIdentity()
    {
        DeviceAxisMap claw = new()
        {
            Swap = new Dictionary<string, string> { ["X"] = "X", ["Y"] = "Z", ["Z"] = "Y" },
            Sign = new Dictionary<string, int> { ["X"] = 1, ["Y"] = 1, ["Z"] = -1 }
        };

        var device = LabMotionAnalysis.ToDeviceFrame(claw);

        AssertMap(new DeviceAxisMap
        {
            Swap = new Dictionary<string, string> { ["X"] = "X", ["Y"] = "Y", ["Z"] = "Z" },
            Sign = new Dictionary<string, int> { ["X"] = 1, ["Y"] = 1, ["Z"] = 1 }
        }, device);
    }

    [Fact]
    public void ToHcFrame_UndoesToDeviceFrame()
    {
        DeviceAxisMap allyX = new()
        {
            Swap = new Dictionary<string, string> { ["X"] = "X", ["Y"] = "Z", ["Z"] = "Y" },
            Sign = new Dictionary<string, int> { ["X"] = -1, ["Y"] = -1, ["Z"] = 1 }
        };

        AssertMap(allyX, LabMotionAnalysis.ToHcFrame(LabMotionAnalysis.ToDeviceFrame(allyX)));
    }

    [Fact]
    public void FirstExcursionSign_FollowsTheFirstLargeMovement()
    {
        Assert.Equal(-1, LabMotionAnalysis.FirstExcursionSign([0, -1, -50, -100, -40, 0, 60, 100, 30, 0]));
        Assert.Equal(1, LabMotionAnalysis.FirstExcursionSign([0, 2, 80, 100, 10, -90, -100, 0]));
        Assert.Equal(0, LabMotionAnalysis.FirstExcursionSign([5, 5, 5, 5]));
    }

    [Fact]
    public void ReportCandidates_FindsTheBytesThatFollowGravity()
    {
        List<(LabMotionStep, LabInputStepRecord)> steps = [];
        var noise = 0;
        foreach (var step in LabMotionSteps.All.Where(item => item.Kind == LabMotionStepKind.Pose))
        {
            List<LabInputEvent> events = [];
            for (var i = 0; i < 6; i++)
            {
                var bytes = new byte[16];
                bytes[0] = 0x01;
                bytes[12] = (byte)(i * 37);
                var value = (short)((step.Axis == 'Z' ? step.Sign * 4000 : 0) + noise++ % 3 - 1);
                bytes[5] = (byte)value;
                bytes[6] = (byte)(value >> 8);
                events.Add(new LabInputEvent(i, "raw-input", "hid0", "report", Convert.ToHexString(bytes)));
            }

            steps.Add((step,
                new LabInputStepRecord { Step = step.Id, StartedMs = 0, EndedMs = 3000, Events = events }));
        }

        var candidates = LabMotionAnalysis.ReportCandidates(steps,
            [new LabInputDevice("hid0", "hid", "1234", "5678", 0xFF00, 1, null, false)]);

        var best = candidates.First(item => item.Kind == "gravity");
        Assert.Equal(5, best.Offset);
        Assert.Equal("Z", best.DeviceAxis);
        Assert.Equal(1, best.Sign);
        Assert.Equal("1234", best.VendorId);
        Assert.DoesNotContain(candidates, item => item.Kind == "gravity" && Math.Abs(item.Offset - 5) == 1);
    }

    [Fact]
    public void Alive_ListsSensorsAndInputDevicesThatSentData()
    {
        LabMotionChannel channel = new(["x", "y", "z"]);
        channel.Begin(0);
        channel.Add(0, long.MinValue, [0, 0, 1]);
        channel.Add(10, long.MinValue, [0, 0, 1]);
        LabInputStepRecord input = new()
        {
            Step = "check",
            StartedMs = 0,
            EndedMs = 1000,
            Events = [new LabInputEvent(1, "raw-input", "hid0", "report", "0102")],
            Repeated = new Dictionary<string, int> { ["hid0"] = 9 }
        };

        var alive = LabMotionAnalysis.Alive(
            [new LabMotionSensorStep { SensorId = Accelerometer.Id, Record = channel.End() }], input,
            [Accelerometer], []);

        Assert.Equal(2, alive.Count);
        Assert.Equal(100d, alive[0].RateHz);
        Assert.Equal(10, alive[1].Readings);
        Assert.StartsWith("Sending data:", LabMotionAnalysis.DescribeAlive(alive), StringComparison.Ordinal);
        Assert.Equal("Nothing is sending motion data right now.", LabMotionAnalysis.DescribeAlive([]));
    }

    [Fact]
    public void StepResult_FlagsAnEmptyStep()
    {
        var step = LabMotionSteps.All[0];
        LabMotionStepRecord record = new()
        {
            Step = step.Id, Kind = step.Kind, StartedAt = Now, Seconds = step.Seconds, Sensors = [], Empty = true
        };

        Assert.StartsWith("Nothing sent any data", LabMotionAnalysis.StepResult(step, record, []),
            StringComparison.Ordinal);
    }

    private static List<LabMotionStepRecord> Steps(int gravitySign)
    {
        List<LabMotionStepRecord> steps = [];
        foreach (var step in LabMotionSteps.All)
        {
            List<LabMotionSensorStep> sensors = [];
            if (step.Kind is LabMotionStepKind.Rest or LabMotionStepKind.Pose)
            {
                // A still sensor reads gravity * up; the Windows convention has gravitySign -1.
                var device = Vector(step.Axis, step.Sign * gravitySign);
                sensors.Add(Still(Accelerometer, ToRaw(device), 0.002));
                sensors.Add(Still(Gyrometer, [0.3, -0.2, 0.1], 0.05));
            }
            else
            {
                sensors.Add(Still(Accelerometer, ToRaw(Vector('Z', gravitySign)), 0.1));
                sensors.Add(Turning(step));
            }

            steps.Add(new LabMotionStepRecord
            {
                Step = step.Id, Kind = step.Kind, StartedAt = Now, Seconds = step.Seconds, Sensors = sensors
            });
        }

        return steps;
    }

    private static LabMotionSensorStep Turning(LabMotionStep step)
    {
        List<double[]> series = [];
        for (var t = 0; t < 100; t++)
        {
            var value = step.Sign * 150 * Math.Sin(2 * Math.PI * t / 50);
            series.Add(ToRaw(Vector(step.Axis, value)));
        }

        var mean = Enumerable.Range(0, 3).Select(i => series.Average(row => row[i])).ToArray();
        var spread = Enumerable.Range(0, 3)
            .Select(i => Math.Sqrt(series.Sum(row => Math.Pow(row[i] - mean[i], 2)) / (series.Count - 1)))
            .ToArray();
        return new LabMotionSensorStep
        {
            SensorId = Gyrometer.Id,
            Record = new LabMotionChannelRecord
            {
                Count = series.Count,
                Fields = [.. Enumerable.Range(0, 3).Select(i => Stats(i, mean[i], spread[i]))],
                Samples = [.. series.Select((row, t) => new double?[] { t, t, row[0], row[1], row[2] })]
            }
        };
    }

    private static LabMotionSensorStep Still(LabMotionSensorInfo sensor, double[] mean, double spread)
    {
        return new LabMotionSensorStep
        {
            SensorId = sensor.Id,
            Record = new LabMotionChannelRecord
            {
                Count = 300,
                SensorRateHz = 100,
                Fields = [.. Enumerable.Range(0, 3).Select(i => Stats(i, mean[i], spread))],
                Samples = [.. Enumerable.Range(0, 5).Select(t => new double?[] { t, t, mean[0], mean[1], mean[2] })]
            }
        };
    }

    private static LabMotionFieldStats Stats(int axis, double mean, double spread)
    {
        return new LabMotionFieldStats("xyz"[axis].ToString(), 300, mean, spread, mean, mean);
    }

    private static double[] Vector(char axis, double value)
    {
        var vector = new double[3];
        vector[axis - 'X'] = value;
        return vector;
    }

    // Inverse of Die: raw X = device Y, raw Y = -device X, raw Z = device Z.
    private static double[] ToRaw(double[] device)
    {
        return [device[1], -device[0], device[2]];
    }

    private static LabMotionSensorInfo Sensor(string id, LabMotionSensorKind kind)
    {
        return new LabMotionSensorInfo
        {
            Id = id,
            Source = "winrt",
            Kind = kind,
            Sampled = true,
            Fields = ["x", "y", "z"],
            AxisFields = [0, 1, 2]
        };
    }

    private static DeviceKnowledgeRecord Record(DeviceMotionKnowledge motion)
    {
        return new DeviceKnowledgeRecord
        {
            SchemaVersion = 1,
            Id = "test.device",
            DisplayName = "Test device",
            Status = DeviceKnowledgeStatus.Curated,
            Motion = motion
        };
    }

    private static void AssertMap(DeviceAxisMap expected, DeviceAxisMap? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Swap.OrderBy(pair => pair.Key), actual.Swap.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Sign.OrderBy(pair => pair.Key), actual.Sign.OrderBy(pair => pair.Key));
    }
}
