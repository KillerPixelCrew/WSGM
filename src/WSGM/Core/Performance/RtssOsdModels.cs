using System;
using System.Collections.Generic;

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

/// <summary>What the richer OSD levels show about the sustained power limit and AutoTDP.</summary>
/// <param name="TdpWatts">The current sustained power limit, when the device reports one.</param>
/// <param name="AutoTdpEnabled">Whether AutoTDP is switched on.</param>
/// <param name="AutoTdpRunning">Whether AutoTDP is actively controlling the limit.</param>
/// <param name="AutoTdpWatts">The limit AutoTDP currently holds or is moving toward.</param>
/// <param name="AutoTdpActivity">Short, display-ready text describing what AutoTDP is doing.</param>
/// <param name="SlowWatts">The slow boost limit (Intel PL2), only when the device publishes one.</param>
/// <param name="FastWatts">The fast boost limit, only when the device publishes one.</param>
internal sealed record RtssOsdPowerStatus(
    int? TdpWatts,
    bool AutoTdpEnabled,
    bool AutoTdpRunning,
    int? AutoTdpWatts,
    string AutoTdpActivity,
    int? SlowWatts = null,
    int? FastWatts = null)
{
    /// <summary>No known limit and no active AutoTDP state.</summary>
    internal static readonly RtssOsdPowerStatus Empty = new(null, false, false, null, string.Empty);
}

/// <summary>
///     The custom overlay's configuration — selector level 4, HandheldCompanion's Custom
///     level: which widgets render, in which order, at which detail. Configured in WSGM's Settings
///     rather than through any RTSS-side mechanism.
/// </summary>
/// <param name="Order">Canonical widget names in render order, one row per name.</param>
/// <param name="Time">Clock detail: 0 hidden, 1 short time, 2 full timestamp.</param>
/// <param name="Fps">Framerate detail: 0 hidden, 1 FPS, 2 FPS and frametime.</param>
/// <param name="Cpu">CPU detail: 0 hidden, 1 load and power, 2 adds temperature.</param>
/// <param name="Ram">Memory detail: 0 hidden, 1 used, 2 used of total.</param>
/// <param name="Gpu">GPU detail: 0 hidden, 1 load and power, 2 adds temperature.</param>
/// <param name="Vram">Video-memory detail: 0 hidden, 1 used, 2 used of total.</param>
/// <param name="Battery">Battery detail: 0 hidden, 1 percent and time, 2 adds charge rate.</param>
internal sealed record RtssOsdCustomSettings(
    IReadOnlyList<string> Order,
    int Time,
    int Fps,
    int Cpu,
    int Ram,
    int Gpu,
    int Vram,
    int Battery)
{
    private static readonly string[] KnownWidgets =
        ["TIME", "GPU", "CPU", "VRAM", "RAM", "BATT", "FPS"];

    /// <summary>HandheldCompanion's defaults: its shipped order, every widget at full detail.</summary>
    internal static readonly RtssOsdCustomSettings Default =
        new("Time,GPU,CPU,VRAM,RAM,BATT,FPS", 2, 2, 2, 2, 2, 2, 2);

    private RtssOsdCustomSettings(
        string KnownWidgets, int Time, int Fps, int Cpu, int Ram, int Gpu, int Vram, int Battery)
        : this(ParseOrder(KnownWidgets), Time, Fps, Cpu, Ram, Gpu, Vram, Battery)
    {
    }

    /// <summary>
    ///     Builds the settings from the persisted configuration, dropping unknown widget
    ///     names and clamping every detail level into range.
    /// </summary>
    /// <param name="configuration">The persisted performance configuration.</param>
    /// <returns>The renderer-ready settings.</returns>
    internal static RtssOsdCustomSettings FromConfig(PerformanceConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new RtssOsdCustomSettings(
            ParseOrder(configuration.OsdCustomOrder),
            Math.Clamp(configuration.OsdCustomTime, 0, 2),
            Math.Clamp(configuration.OsdCustomFps, 0, 2),
            Math.Clamp(configuration.OsdCustomCpu, 0, 2),
            Math.Clamp(configuration.OsdCustomRam, 0, 2),
            Math.Clamp(configuration.OsdCustomGpu, 0, 2),
            Math.Clamp(configuration.OsdCustomVram, 0, 2),
            Math.Clamp(configuration.OsdCustomBattery, 0, 2));
    }

    private static List<string> ParseOrder(string? order)
    {
        List<string> names = [];
        foreach (var part in (order ?? string.Empty).Split(
                     ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = part.ToUpperInvariant();
            if (Array.IndexOf(KnownWidgets, canonical) >= 0 && !names.Contains(canonical))
            {
                names.Add(canonical);
            }
        }

        return names;
    }
}
