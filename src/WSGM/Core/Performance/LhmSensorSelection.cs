using LibreHardwareMonitor.Hardware;

namespace WSGM.Core;

/// <summary>
///     Collects one LibreHardwareMonitor update's sensors with HandheldCompanion's sensor-name rules
///     (<c>LibreHardwarePlatform</c>); fields with no matching sensor stay null.
/// </summary>
internal sealed class LhmSensorSelection
{
    internal const double Gib = 1024.0 * 1024 * 1024;

    private double? _cpuLoad, _cpuPower, _cpuTemp;
    private double? _gpuLoad, _gpuPower, _gpuTemp;
    private double? _gpuMemoryUsedBytes, _gpuMemoryTotalBytes;
    private int _gpuMemoryUsedRank = int.MaxValue, _gpuMemoryTotalRank = int.MaxValue;

    /// <summary>Considers one sensor reading.</summary>
    /// <param name="hardware">The hardware group the sensor belongs to.</param>
    /// <param name="type">The sensor's kind.</param>
    /// <param name="name">The sensor's LibreHardwareMonitor name.</param>
    /// <param name="value">The current reading.</param>
    internal void Add(HardwareType hardware, SensorType type, string name, double value)
    {
        if (hardware is HardwareType.Cpu)
        {
            // HC: total load, package power, package temperature.
            switch (type)
            {
                case SensorType.Load when name == "CPU Total":
                    _cpuLoad = value;
                    break;
                case SensorType.Power when name is "CPU Package" or "Package":
                    _cpuPower = value;
                    break;
                case SensorType.Temperature when name is "CPU Package" or "Core (Tctl/Tdie)":
                    _cpuTemp = value;
                    break;
            }
        }
        else if (hardware is HardwareType.GpuIntel or HardwareType.GpuAmd or HardwareType.GpuNvidia)
        {
            switch (type)
            {
                case SensorType.Load when name == "D3D 3D" || (name == "GPU Core" && _gpuLoad is null):
                    _gpuLoad = name == "D3D 3D" ? value : _gpuLoad ?? value;
                    break;
                case SensorType.Power when _gpuPower is null
                                           && name is "GPU Power" or "GPU Package" or "GPU Core" or "GPU SoC":
                    _gpuPower = value;
                    break;
                case SensorType.Temperature when name == "GPU Core":
                    _gpuTemp = value;
                    break;
                case SensorType.Data:
                {
                    // HC's preference order: dedicated GPU memory beats D3D dedicated beats
                    // D3D shared; this library version reports bytes.
                    var usedRank = name switch
                    {
                        "GPU Memory Used" => 0,
                        "D3D Dedicated Memory Used" => 1,
                        "D3D Shared Memory Used" => 2,
                        _ => -1
                    };
                    if (usedRank >= 0 && usedRank < _gpuMemoryUsedRank)
                    {
                        _gpuMemoryUsedRank = usedRank;
                        _gpuMemoryUsedBytes = value;
                    }

                    var totalRank = name switch
                    {
                        "GPU Memory Total" => 0,
                        "D3D Dedicated Memory Total" => 1,
                        "D3D Shared Memory Total" => 2,
                        _ => -1
                    };
                    if (totalRank >= 0 && totalRank < _gpuMemoryTotalRank)
                    {
                        _gpuMemoryTotalRank = totalRank;
                        _gpuMemoryTotalBytes = value;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>Returns the selected values; system memory and battery stay kernel-fed.</summary>
    /// <returns>The partial metrics.</returns>
    internal RtssOsdMetrics Build()
    {
        return RtssOsdMetrics.Empty with
        {
            CpuLoadPercent = _cpuLoad,
            CpuPowerWatts = _cpuPower,
            CpuTemperatureC = _cpuTemp,
            GpuLoadPercent = _gpuLoad,
            GpuPowerWatts = _gpuPower,
            GpuTemperatureC = _gpuTemp,
            GpuMemoryUsedGb = _gpuMemoryUsedBytes / Gib,
            GpuMemoryTotalGb = _gpuMemoryTotalBytes / Gib
        };
    }
}
