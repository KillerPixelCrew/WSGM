using System;
using System.Collections.Generic;

namespace WSGM.Core;

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

    /// <summary>
    ///     The configuration defaults (HandheldCompanion's shipped order, every widget at full detail),
    ///     taken from <see cref="PerformanceConfig" /> so there is one copy.
    /// </summary>
    internal static readonly RtssOsdCustomSettings Default = FromConfig(new PerformanceConfig());

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
