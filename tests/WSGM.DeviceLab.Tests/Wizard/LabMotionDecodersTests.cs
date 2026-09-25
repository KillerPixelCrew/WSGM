using System.Buffers.Binary;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabMotionDecodersTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryDecode_ReadsTheNeptuneImuWithOrWithoutAReportIdByte(bool unnumbered)
    {
        var layout = Assert.Single(LabMotionDecoders.Controllers, item => item.Name == "neptune");
        var hid = new byte[64];
        hid[0] = 0x01;
        hid[2] = 0x09;
        BinaryPrimitives.WriteInt16LittleEndian(hid.AsSpan(24), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(hid.AsSpan(30), -3277);
        byte[] report = unnumbered ? [0, .. hid] : hid;
        var accelerometer = new double[3];
        var gyrometer = new double[3];

        Assert.True(LabMotionDecoders.TryDecode(layout, report, accelerometer, gyrometer));

        Assert.Equal(1.0, accelerometer[0], 3);
        Assert.Equal(-200.0, gyrometer[0], 1);
    }

    [Fact]
    public void TryDecode_RejectsAnotherNeptunePacket()
    {
        var layout = Assert.Single(LabMotionDecoders.Controllers, item => item.Name == "neptune");
        var report = new byte[64];
        report[0] = 0x01;
        report[2] = 0x04;

        Assert.False(LabMotionDecoders.TryDecode(layout, report, new double[3], new double[3]));
    }

    [Fact]
    public void TryDecode_ReadsLegionValuesBigEndian()
    {
        var layout = Assert.Single(LabMotionDecoders.Controllers, item => item.Name == "legion-left");
        var report = new byte[64];
        report[0] = 0x04;
        BinaryPrimitives.WriteInt16BigEndian(report.AsSpan(35), -8192);
        var accelerometer = new double[3];

        Assert.True(LabMotionDecoders.TryDecode(layout, report, accelerometer, new double[3]));

        Assert.Equal(-1.0, accelerometer[0], 6);
    }

    [Fact]
    public void LayoutsFor_MatchesVendorProductAndReportLength()
    {
        Assert.Equal(new[] { "legion-left", "legion-right" },
            LabMotionDecoders.LayoutsFor(0x17EF, 0x6182, 64).Select(layout => layout.Name));
        Assert.Empty(LabMotionDecoders.LayoutsFor(0x17EF, 0x6182, 33));
        Assert.Empty(LabMotionDecoders.LayoutsFor(0x0B05, 0x1B4C, 64));
    }

    [Fact]
    public void SerialFrames_AreFoundInAStreamAndDecoded()
    {
        var frame = new byte[LabMotionDecoders.SerialFrameLength];
        frame[0] = 0xA4;
        frame[1] = 0x03;
        frame[2] = 0x08;
        frame[3] = 0x12;
        BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(4), 2048);
        BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(10), -16384);
        byte sum = 0;
        foreach (var value in frame.AsSpan(0, frame.Length - 1))
        {
            sum += value;
        }

        frame[^1] = sum;
        byte[] stream = [0x00, 0xA4, 0x01, .. frame, 0xA4, 0x03];

        Assert.True(LabMotionDecoders.FindSerialFrame(stream, out var start));
        Assert.Equal(3, start);
        var accelerometer = new double[3];
        var gyrometer = new double[3];
        Assert.True(LabMotionDecoders.DecodeSerialFrame(stream.AsSpan(start), accelerometer, gyrometer));
        Assert.Equal(1.0, accelerometer[0], 6);
        Assert.Equal(-1000.0, gyrometer[0], 6);

        Assert.False(LabMotionDecoders.FindSerialFrame(stream.AsSpan(start + frame.Length), out var keep));
        Assert.Equal(0, keep);
    }

    [Fact]
    public void SerialAssignedToControl_FollowsTheRecordsHazard()
    {
        DeviceKnowledgeRecord record = new()
        {
            SchemaVersion = 1,
            Id = "test.x1",
            DisplayName = "X1",
            Status = DeviceKnowledgeStatus.Extracted,
            Hazards = ["The X1 opens its own CH340 serial port (115200) for LED control. Do not probe it as a serial IMU."]
        };

        Assert.True(LabMotionDecoders.SerialAssignedToControl(record));
        Assert.False(LabMotionDecoders.SerialAssignedToControl(record with { Hazards = [] }));
        Assert.False(LabMotionDecoders.SerialAssignedToControl(null));
    }

    [Fact]
    public void LegacyAxes_UsesTheRecordsCustomFieldsFirst()
    {
        const string custom = "b14c764f-07cf-41e8-9d82-ebe3d0776a6f";
        string[] fields = [$"{custom}:34", $"{custom}:7", $"{custom}:8", $"{custom}:9"];
        DeviceMotionKnowledge known = new()
        {
            LegacyFields =
            [
                new DeviceLegacySensorFields
                {
                    Kind = "gyrometer", FriendlyName = "Physical Gyrometer", FormatId = custom, PropertyIds = [7, 8, 9]
                }
            ]
        };

        var (kind, axes, source, _) = LabMotionRecorder.LegacyAxes("Physical Gyrometer",
            new Guid("E83AF229-8640-4D18-A213-E22675EBB2C3"), fields, known);

        Assert.Equal(LabMotionSensorKind.Gyrometer, kind);
        Assert.Equal(new[] { 1, 2, 3 }, axes);
        Assert.Equal("knowledge", source);
    }

    [Fact]
    public void LegacyAxes_FindsTheStandardAccelerationFields()
    {
        const string motion = "3f8a69a2-07c5-4e48-a965-cd797aab56d5";
        string[] fields = [$"{motion}:2", $"{motion}:3", $"{motion}:4"];

        var (kind, axes, source, units) = LabMotionRecorder.LegacyAxes("Accelerometer", Guid.Empty, fields, null);

        Assert.Equal(LabMotionSensorKind.Accelerometer, kind);
        Assert.Equal(new[] { 0, 1, 2 }, axes);
        Assert.Equal("standard", source);
        Assert.Equal("g", units);
    }
}
