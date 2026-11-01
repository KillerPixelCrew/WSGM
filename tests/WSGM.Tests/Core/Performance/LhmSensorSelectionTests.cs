using LibreHardwareMonitor.Hardware;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class LhmSensorSelectionTests
{
    private const double Mib = 1024.0 * 1024;

    [Fact]
    public void SelectsHandheldCompanionsSensors()
    {
        // LibreHardwareMonitor 0.9.7's names on the Atlas (Panther Lake, Arc B370), plus the Claw's
        // D3D memory sensors.
        LhmSensorSelection selection = new();
        selection.Add(HardwareType.Cpu, SensorType.Load, "CPU Total", 17.4);
        selection.Add(HardwareType.Cpu, SensorType.Load, "CPU Core #1", 37.5);
        selection.Add(HardwareType.Cpu, SensorType.Temperature, "Core Max", 61);
        selection.Add(HardwareType.Cpu, SensorType.Temperature, "CPU Package", 58);
        selection.Add(HardwareType.Cpu, SensorType.Power, "CPU Package", 12.6);
        selection.Add(HardwareType.Cpu, SensorType.Power, "CPU Platform", 19.6);
        selection.Add(HardwareType.GpuIntel, SensorType.Power, "GPU Power", 0.3);
        selection.Add(HardwareType.GpuIntel, SensorType.Load, "D3D 3D", 8.3);
        selection.Add(HardwareType.GpuIntel, SensorType.Load, "D3D Video Decode", 99);
        selection.Add(HardwareType.GpuIntel, SensorType.Data, "D3D Shared Memory Used", 1264.8 * Mib);
        selection.Add(HardwareType.GpuIntel, SensorType.Data, "D3D Shared Memory Total", 18409.7 * Mib);

        var metrics = selection.Build();

        Assert.Equal(17.4, metrics.CpuLoadPercent!.Value, 3);
        Assert.Equal(12.6, metrics.CpuPowerWatts!.Value, 3);
        Assert.Equal(58.0, metrics.CpuTemperatureC!.Value, 3);
        Assert.Equal(8.3, metrics.GpuLoadPercent!.Value, 3);
        Assert.Equal(0.3, metrics.GpuPowerWatts!.Value, 3);
        Assert.Null(metrics.GpuTemperatureC);
        Assert.Equal(1264.8 / 1024.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(18409.7 / 1024.0, metrics.GpuMemoryTotalGb!.Value, 3);
        // System memory and battery are kernel-fed.
        Assert.Null(metrics.MemoryUsedGb);
        Assert.Null(metrics.BatteryPercent);
    }

    [Fact]
    public void PrefersDedicatedGpuMemoryOverShared()
    {
        LhmSensorSelection selection = new();
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "D3D Shared Memory Used", 2048 * Mib);
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "GPU Memory Used", 1024 * Mib);
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "GPU Memory Total", 4096 * Mib);

        var metrics = selection.Build();

        Assert.Equal(1.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(4.0, metrics.GpuMemoryTotalGb!.Value, 3);
    }

    [Fact]
    public void IgnoresSensorsOutsideCpuAndGpu()
    {
        LhmSensorSelection selection = new();
        selection.Add(HardwareType.Motherboard, SensorType.Temperature, "CPU Package", 90);
        selection.Add(HardwareType.Memory, SensorType.Power, "GPU Power", 5);

        Assert.Equal(RtssOsdMetrics.Empty, selection.Build());
    }
}
