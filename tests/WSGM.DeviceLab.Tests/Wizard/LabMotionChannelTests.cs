using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabMotionChannelTests
{
    [Fact]
    public void End_ReportsMeanAndDeviationOverEveryReading()
    {
        LabMotionChannel channel = new(["x", "y"]);
        channel.Begin(0);
        channel.Add(10, long.MinValue, [1, 10]);
        channel.Add(20, long.MinValue, [3, 10]);

        var record = channel.End();

        Assert.Equal(2, record.Count);
        Assert.Equal(2d, record.Fields[0].Mean);
        Assert.Equal(Math.Round(Math.Sqrt(2), 6), record.Fields[0].StdDev);
        Assert.Equal(0d, record.Fields[1].StdDev);
        Assert.Equal(100d, record.HostRateHz);
        Assert.Null(record.SensorRateHz);
    }

    [Fact]
    public void Add_IgnoresReadingsOutsideAStep()
    {
        LabMotionChannel channel = new(["x"]);
        channel.Add(1, long.MinValue, [5]);
        channel.Begin(2);
        channel.Add(3, long.MinValue, [7]);
        var record = channel.End();
        channel.Add(4, long.MinValue, [9]);

        Assert.Equal(1, record.Count);
        Assert.Equal(7d, record.Fields[0].Mean);
        Assert.False(channel.Active);
    }

    [Fact]
    public void Add_KeepsBoundedEvenlySpacedSamples()
    {
        LabMotionChannel channel = new(["x"]);
        channel.Begin(0);
        const int readings = LabMotionChannel.MaximumSamples * 5;
        for (var i = 0; i < readings; i++)
        {
            channel.Add(i, i * TimeSpan.TicksPerMillisecond, [i]);
        }

        var record = channel.End();

        Assert.Equal(readings, record.Count);
        Assert.True(record.Samples.Count <= LabMotionChannel.MaximumSamples);
        Assert.True(record.SampleStride > 1);
        Assert.Equal(0d, record.Samples[0][2]);
        Assert.True(record.Samples[^1][2] > readings * 0.9);
        Assert.Equal(1000d, record.SensorRateHz);
        Assert.All(record.Samples.Zip(record.Samples.Skip(1)),
            pair => Assert.Equal(record.SampleStride, pair.Second[2]!.Value - pair.First[2]!.Value));
    }

    [Fact]
    public void End_WritesMissingValuesAsNull()
    {
        LabMotionChannel channel = new(["x", "y"]);
        channel.Begin(0);
        channel.Add(1, long.MinValue, [double.NaN, 2]);

        var record = channel.End();

        Assert.Equal(0, record.Fields[0].Count);
        Assert.Null(record.Fields[0].Mean);
        Assert.Null(record.Samples[0][1]);
        Assert.Null(record.Samples[0][2]);
        Assert.Equal(2d, record.Samples[0][3]);
    }

    [Fact]
    public void Begin_ClearsTheLastStep()
    {
        LabMotionChannel channel = new(["x"]);
        channel.Begin(0);
        channel.Add(1, long.MinValue, [100]);
        channel.CountDuplicate();
        channel.End();

        channel.Begin(10);
        channel.Add(11, long.MinValue, [1]);
        var record = channel.End();

        Assert.Equal(1, record.Count);
        Assert.Equal(0L, record.Duplicates);
        Assert.Equal(1d, record.Fields[0].Mean);
        Assert.Equal(1d, record.Samples[0][0]);
    }
}
