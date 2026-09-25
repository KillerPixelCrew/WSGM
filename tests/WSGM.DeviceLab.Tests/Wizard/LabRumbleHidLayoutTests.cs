using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabRumbleHidLayoutTests
{
    [Fact]
    public void AllyXLayout_PlacesPercentagesAndPadsToTheOutputLength()
    {
        var layout = LabRumbleHidLayout.TryParse("0D 0F 00 00 <left%> <right%> FF 00 EB", out var problem);

        Assert.Null(problem);
        Assert.NotNull(layout);
        var report = layout.Encode(new LabRumbleFrame(60, 25), 64);
        Assert.Equal(64, report.Length);
        Assert.Equal("0D0F00003C19FF00EB", Convert.ToHexString(report, 0, 9));
        Assert.All(report[9..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void ClawLayout_ScalesStrongAndWeakToAByte()
    {
        var layout = LabRumbleHidLayout.TryParse("05 01 00 00 <weak> <strong> 00 00 00 00 00", out _);

        Assert.NotNull(layout);
        var report = layout.Encode(new LabRumbleFrame(100, 50), 11);
        Assert.Equal(0x80, report[4]);
        Assert.Equal(0xFF, report[5]);
        Assert.Equal(0, layout.Encode(LabRumbleFrame.Zero, 11)[4..6].Sum(value => value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0D 0F <left%>")]
    [InlineData("<left%> <right%>")]
    [InlineData("0D <left%> <right%> <level>")]
    [InlineData("0D <left%> <right%> 1FF")]
    public void UnknownOrIncompleteLayouts_AreRefused(string? text)
    {
        Assert.Null(LabRumbleHidLayout.TryParse(text, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Encode_RejectsOutOfRangeStrengthAndShortCollections()
    {
        var layout = LabRumbleHidLayout.TryParse("0D 0F 00 00 <left%> <right%> FF 00 EB", out _)!;

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Encode(new LabRumbleFrame(101, 0), 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Encode(new LabRumbleFrame(0, -1), 64));
        Assert.Throws<InvalidOperationException>(() => layout.Encode(new LabRumbleFrame(10, 10), 8));
    }

    [Fact]
    public void Summary_NamesTheRouteFloorAndPulse()
    {
        LabRumbleRouteCalibration calibration = new() { Route = "xinput:0", Name = "XInput", Swapped = false };
        foreach (var side in new[] { "left", "right" })
        {
            LabRumbleSideCalibration calibrated = new() { Side = side, Channel = side, MinimumStartIntensityPercent = 18 };
            calibrated.Pulses.Add(new LabRumblePulseTrial { Strength = "full", Percent = 100, Milliseconds = 10, Felt = false });
            calibrated.Pulses.Add(new LabRumblePulseTrial { Strength = "full", Percent = 100, Milliseconds = 25, Felt = true });
            calibrated.Pulses.Add(new LabRumblePulseTrial { Strength = "lowest", Percent = 18, Milliseconds = 10, Felt = true });
            calibration.Sides.Add(calibrated);
        }

        Assert.Equal(25, calibration.Sides[0].MinimumPulseMilliseconds);
        Assert.Equal(10, calibration.Sides[0].MinimumPulseAtLowestMilliseconds);
        Assert.Equal("XInput works; floor 18 %, shortest pulse 25 ms.",
            LabRumbleSummary.Describe(2, ["XInput"], [calibration]));
    }

    [Fact]
    public void Summary_NamesEachSideWhenTheyDiffer()
    {
        LabRumbleRouteCalibration calibration = new() { Route = "xinput:0", Name = "XInput", Swapped = true };
        calibration.Sides.Add(new LabRumbleSideCalibration { Side = "left", Channel = "right", MinimumStartIntensityPercent = 18 });
        calibration.Sides.Add(new LabRumbleSideCalibration { Side = "right", Channel = "left", MinimumStartIntensityPercent = 22 });

        Assert.Equal("XInput works; left and right motors are swapped, floor left 18 %, right 22 %.",
            LabRumbleSummary.Describe(1, ["XInput"], [calibration]));
    }

    [Fact]
    public void Summary_SaysWhenNothingWasFoundOrFelt()
    {
        Assert.Equal("No way to drive the motors was found. Nothing was written.",
            LabRumbleSummary.Describe(0, [], []));
        Assert.Equal("No rumble was felt on any of the 3 ways tried.", LabRumbleSummary.Describe(3, [], []));
    }
}
