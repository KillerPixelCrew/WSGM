using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssOsdCustomTests
{
    private static readonly RtssOsdMetrics Metrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 40,
        CpuPowerWatts = 12,
        GpuLoadPercent = 30,
        GpuPowerWatts = 8,
        GpuMemoryUsedGb = 1.5,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 10.0,
        MemoryTotalGb = 32.0,
        BatteryPercent = 80,
        BatteryWatts = -12,
        BatteryMinutesRemaining = 60,
        OnAcPower = false
    };

    [Fact]
    public void FromConfig_ParsesOrderAndDropsUnknownNames()
    {
        var config = new PerformanceConfig
        {
            OsdCustomOrder = "fps, cpu, Nonsense, CPU, batt",
            OsdCustomFps = 7,
            OsdCustomBattery = -3
        };

        var settings = RtssOsdCustomSettings.FromConfig(config);

        Assert.Equal(["FPS", "CPU", "BATT"], settings.Order);
        Assert.Equal(2, settings.Fps);
        Assert.Equal(0, settings.Battery);
    }

    [Fact]
    public void BuildCustom_RendersOneRowPerConfiguredWidgetInOrder()
    {
        var settings = RtssOsdCustomSettings.Default;

        var text = RtssOsdContent.BuildCustom(settings, Metrics);

        var rows = text.Split('\n');
        // Default order Time,GPU,CPU,VRAM,RAM,BATT,FPS — every widget has a source here.
        Assert.Equal(7, rows.Length);
        Assert.Contains("TIME", rows[0]);
        Assert.Contains("GPU", rows[1]);
        Assert.Contains("CPU", rows[2]);
        Assert.Contains("VRAM", rows[3]);
        Assert.Contains("RAM", rows[4]);
        Assert.Contains("BATT", rows[5]);
        Assert.Contains("FPS", rows[6]);
        Assert.Contains("<FR>", rows[6]);
    }

    [Fact]
    public void BuildCustom_PrependsPowerStatus()
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Lowering");

        var text = RtssOsdContent.BuildCustom(
            RtssOsdCustomSettings.Default,
            Metrics,
            power);

        var rows = text.Split('\n');
        Assert.Contains("TDP", rows[0]);
        Assert.Contains("18<S1>W", rows[0]);
        Assert.Contains("AUTO TDP", rows[1]);
        Assert.Contains("17<S1>W", rows[1]);
        Assert.Contains("LOWERING", rows[1]);
    }

    [Fact]
    public void BuildCustom_SkipsHiddenWidgetsAndSourcelessEntries()
    {
        var settings = RtssOsdCustomSettings.Default with { Gpu = 0 };
        var noBattery = Metrics with { BatteryPercent = null, BatteryWatts = null };

        var text = RtssOsdContent.BuildCustom(settings, noBattery);

        Assert.DoesNotContain("GPU", text.Replace("VRAM", string.Empty));
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("CPU", text);
    }
}
