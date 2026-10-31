using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssOsdContentTests
{
    private static readonly RtssOsdMetrics FullMetrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 42.5,
        CpuPowerWatts = 13.7,
        CpuTemperatureC = 61,
        GpuLoadPercent = 55.2,
        GpuPowerWatts = 9.8,
        GpuMemoryUsedGb = 1.2,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 12.3,
        MemoryTotalGb = 31.6,
        BatteryPercent = 76,
        BatteryWatts = -14.2,
        BatteryMinutesRemaining = 95,
        OnAcPower = false
    };

    private static readonly RtssOsdMetrics EmptyMetrics = RtssOsdMetrics.Empty with
    {
        OnAcPower = true
    };

    [Fact]
    public void Minimal_IsOneRowOfRtssFramerate()
    {
        var text = RtssOsdContent.Build(1, EmptyMetrics);

        Assert.StartsWith("<C0=FFFFFF><C1=8000FF>", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<APP>", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("<FT>", text);
    }

    [Fact]
    public void Minimal_DoesNotShowPowerStatus()
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Raising");

        var text = RtssOsdContent.Build(1, FullMetrics, power);

        Assert.DoesNotContain("TDP", text);
        Assert.DoesNotContain("RAISING", text);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RichLevels_ShowTdpAndLiveAutoTdpActivity(int level)
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Raising");

        var text = RtssOsdContent.Build(level, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.Contains("18<S1>W", text);
        Assert.Contains("AUTO TDP", text);
        Assert.Contains("17<S1>W", text);
        Assert.Contains("RAISING", text);
    }

    [Fact]
    public void Tdp_ShowsPublishedBoostLimitsAfterSustained()
    {
        var power = new RtssOsdPowerStatus(25, false, false, null, string.Empty, SlowWatts: 37);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("<C0>25/37<S1>W", text);
    }

    [Fact]
    public void AutoTdpIndicator_IsHiddenWhileAutoTdpIsOff()
    {
        var power = new RtssOsdPowerStatus(18, false, false, null, string.Empty);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.DoesNotContain("AUTO TDP", text);
    }

    [Fact]
    public void AutoTdpIndicator_IsHiddenUntilAutoTdpIsActivelyControlling()
    {
        var power = new RtssOsdPowerStatus(18, true, false, null, string.Empty);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.DoesNotContain("AUTO TDP", text);
    }

    [Fact]
    public void Extended_IsOneRowWithEverySourcedSubject()
    {
        var text = RtssOsdContent.Build(2, FullMetrics);

        Assert.DoesNotContain("\n", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<FT>", text);
        Assert.Contains("GPU", text);
        Assert.Contains("VRAM", text);
        Assert.Contains("CPU", text);
        Assert.Contains("RAM", text);
        Assert.Contains("BATT", text);
        // Entries are joined with HC's separator.
        Assert.Contains("<C1> | <C>", text);
    }

    [Fact]
    public void Full_IsOneRowPerSubject()
    {
        var text = RtssOsdContent.Build(3, FullMetrics);

        var rows = text.Split('\n');
        Assert.Equal(6, rows.Length);
        // Full shows used/total memory, temperatures and the discharge estimate.
        Assert.Contains("12.3/31.6", text);
        Assert.Contains("1.2/18.0", text);
        Assert.Contains("61<S1>C", text);
        Assert.Contains("01<S1>h", text);
        Assert.Contains("35<S1>min", text);
    }

    [Fact]
    public void SubjectsWithoutASourceDoNotRender()
    {
        var text = RtssOsdContent.Build(3, EmptyMetrics);

        // Only the FPS row survives: its tags are filled by RTSS, not by a sensor.
        Assert.DoesNotContain("GPU", text);
        Assert.DoesNotContain("CPU", text);
        Assert.DoesNotContain("RAM", text);
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("<FR>", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void BatteryTimeIsOmittedOnAcPower()
    {
        var charging = FullMetrics with { OnAcPower = true };

        var text = RtssOsdContent.Build(3, charging);

        Assert.DoesNotContain("<S1>h", text);
        Assert.DoesNotContain("<S1>min", text);
        Assert.Contains("BATT", text);
    }

    [Fact]
    public void UnrenderedLevelsAreEmpty()
    {
        Assert.Equal(string.Empty, RtssOsdContent.Build(0, FullMetrics));
        Assert.Equal(string.Empty, RtssOsdContent.Build(4, FullMetrics));
    }
}
