using System;

namespace WSGM.Core;

/// <summary>
///     One sensor sample for the OSD and AutoTDP. Null means unavailable and omits that OSD element.
/// </summary>
/// <param name="CpuLoadPercent">CPU utilization percentage.</param>
/// <param name="CpuPowerWatts">CPU power draw in watts.</param>
/// <param name="CpuTemperatureC">CPU temperature in Celsius.</param>
/// <param name="GpuLoadPercent">GPU utilization percentage.</param>
/// <param name="GpuPowerWatts">GPU power draw in watts.</param>
/// <param name="GpuTemperatureC">GPU temperature in Celsius.</param>
/// <param name="GpuMemoryUsedGb">Used graphics memory in GiB.</param>
/// <param name="GpuMemoryTotalGb">Total graphics memory in GiB.</param>
/// <param name="MemoryUsedGb">Used system memory in GiB, from Windows.</param>
/// <param name="MemoryTotalGb">Total system memory in GiB, from Windows.</param>
/// <param name="BatteryPercent">Remaining battery percentage.</param>
/// <param name="BatteryWatts">Windows battery charge rate in watts; negative values indicate discharge.</param>
/// <param name="BatteryMinutesRemaining">Estimated battery runtime in minutes.</param>
/// <param name="OnAcPower">Whether the source reports AC power; false in the empty sample.</param>
internal sealed record RtssOsdMetrics(
    double? CpuLoadPercent,
    double? CpuPowerWatts,
    double? CpuTemperatureC,
    double? GpuLoadPercent,
    double? GpuPowerWatts,
    double? GpuTemperatureC,
    double? GpuMemoryUsedGb,
    double? GpuMemoryTotalGb,
    double? MemoryUsedGb,
    double? MemoryTotalGb,
    double? BatteryPercent,
    double? BatteryWatts,
    int? BatteryMinutesRemaining,
    bool OnAcPower)
{
    /// <summary>No available sensor values, with AC state unset to false.</summary>
    internal static readonly RtssOsdMetrics Empty = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, false);

    /// <summary>When the shared sensor snapshot was read; cached reads retain this time.</summary>
    internal DateTimeOffset CapturedAt { get; init; }
}
