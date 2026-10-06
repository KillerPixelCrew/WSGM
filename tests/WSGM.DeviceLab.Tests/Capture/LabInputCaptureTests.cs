using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Tests.Capture;

public sealed class LabInputCaptureTests
{
    private static readonly LabInputDevice Device = new("hid0", "hid", null, null, 1, 5, null, false);

    [Fact]
    public void ChangedReports_AreKeptWholeBeyondTheOldStepAndReportCaps()
    {
        using LabInputCapture capture = new(0);
        capture.BeginStep("buttons/test");
        var bytes = new byte[600];
        bytes[0] = 1;
        for (var i = 0; i < 7_000; i++)
        {
            bytes[^1] = (byte)i;
            capture.OnHidReport(Device, bytes);
        }

        var record = capture.EndStep();

        Assert.Equal(7_000, record.Events.Count);
        Assert.All(record.Events, item => Assert.Equal(1_200, item.Data!.Length));
        Assert.Equal(Convert.ToHexString(bytes), record.Events[^1].Data);
    }

    [Fact]
    public void NoiseOnlyReports_RemainSampledAndCountedBeyondTheOldNoiseCap()
    {
        using LabInputCapture capture = new(0);
        capture.BeginStep("buttons/baseline", true);
        capture.OnHidReport(Device, [1, 0]);
        capture.OnHidReport(Device, [1, 1]);
        capture.EndStep();
        capture.BeginStep("buttons/test");
        for (var i = 0; i < 10_001; i++)
        {
            capture.OnHidReport(Device, [1, (byte)(i + 2)]);
        }

        var record = capture.EndStep();

        Assert.Equal(10_001, record.Repeated[Device.Id]);
        Assert.Equal(201, record.Events.Count);
        Assert.All(record.Events, item => Assert.True(item.Noise));
    }

    [Fact]
    public void EndStep_StopsBaselineLearning()
    {
        using LabInputCapture capture = new(0);
        capture.BeginStep("buttons/baseline", true);
        capture.OnHidReport(Device, [1, 0, 0]);
        capture.OnHidReport(Device, [1, 1, 0]);
        capture.EndStep();
        capture.OnHidReport(Device, [1, 1, 1]);

        Assert.Equal([1], capture.NoiseMap()[Device.Id]);
    }

    [Fact]
    public void KeyReleasedWithSwallowingOff_IsNotHeldInTheNextStep()
    {
        using LabInputCapture capture = new(0) { SwallowShortcuts = true };
        Assert.True(capture.OnKey(0x5B, false));
        capture.SwallowShortcuts = false;
        Assert.False(capture.OnKey(0x5B, true));
        capture.SwallowShortcuts = true;

        Assert.False(capture.OnKey(0x41, false));
    }
}
