using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     Builds the OSD text for the WSGM-rendered overlay levels. The structure, tags, colors and
///     per-level layout are HandheldCompanion's (<c>OSDManager</c> / <c>Overlay/Strategy</c>): level 1
///     is Minimal (FPS), level 2 Extended (one combined row) and level 3 Full (one row per subject).
///     <c>&lt;FR&gt;</c>/<c>&lt;FT&gt;</c> are RTSS's own framerate/frametime tags, filled per hooked
///     application, so FPS needs no sensor. Pure, for the tests.
/// </summary>
internal static class RtssOsdContent
{
    // HC's header: C0 default text, C1 separator, alignment and the two script sizes.
    private const string Header = "<C0=FFFFFF><C1=8000FF><A0=-4><S0=-50><S1=50>";
    private const string FpsColor = "FF0000";
    private const string GpuColor = "8040";
    private const string VramColor = "8000FF";
    private const string CpuColor = "80FF";
    private const string RamColor = "FF80C0";
    private const string BattColor = "FF8000";
    private const string TdpColor = "FFD000";
    private const string AutoTdpColor = "00FF80";

    /// <summary>Builds the OSD text for one rendered level.</summary>
    /// <param name="level">1 to 3; anything else yields an empty display.</param>
    /// <param name="metrics">The current sample.</param>
    /// <param name="powerStatus">Current device-power status for levels above 1.</param>
    /// <returns>The RTSS-tagged text.</returns>
    internal static string Build(
        int level,
        RtssOsdMetrics metrics,
        RtssOsdPowerStatus? powerStatus = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        powerStatus ??= RtssOsdPowerStatus.Empty;
        return level switch
        {
            1 => Compose(MinimalFpsRow()),
            2 => Compose(ExtendedRow(metrics, powerStatus)),
            3 => Compose(
                Row(TdpEntry(powerStatus, true)),
                Row(AutoTdpEntry(powerStatus, true)),
                Row(Entry("GPU", GpuColor, true, GpuElements(metrics, true))),
                Row(Entry("CPU", CpuColor, true, CpuElements(metrics, true))),
                Row(Entry("RAM", RamColor, true, RamElements(metrics, true))),
                Row(Entry("VRAM", VramColor, true, VramElements(metrics, true))),
                Row(Entry("BATT", BattColor, true, BatteryElements(metrics, true))),
                Row(Entry("<APP>", FpsColor, true, FpsElements(true)))),
            _ => string.Empty
        };
    }

    private static string MinimalFpsRow()
    {
        return Row(Entry("<APP>", FpsColor, false, FpsElements(false)));
    }

    // Keep HC's subject order after WSGM's live power entries; every entry uses minimal detail.
    private static string ExtendedRow(
        RtssOsdMetrics metrics,
        RtssOsdPowerStatus powerStatus)
    {
        return Row(
            Entry("<APP>", FpsColor, false, FpsElements(true)),
            TdpEntry(powerStatus, false),
            AutoTdpEntry(powerStatus, false),
            Entry("GPU", GpuColor, false, GpuElements(metrics, false)),
            Entry("VRAM", VramColor, false, VramElements(metrics, false)),
            Entry("CPU", CpuColor, false, CpuElements(metrics, false)),
            Entry("RAM", RamColor, false, RamElements(metrics, false)),
            Entry("BATT", BattColor, false, BatteryElements(metrics, false)));
    }

    /// <summary>
    ///     Builds the user-configured Custom overlay — HandheldCompanion's Custom level:
    ///     one row per configured widget name, each at its own detail.
    /// </summary>
    /// <param name="custom">The order and per-widget detail from WSGM's Settings.</param>
    /// <param name="metrics">The current sample.</param>
    /// <param name="powerStatus">Current device-power status, prepended when available.</param>
    /// <returns>The RTSS-tagged text.</returns>
    internal static string BuildCustom(
        RtssOsdCustomSettings custom,
        RtssOsdMetrics metrics,
        RtssOsdPowerStatus? powerStatus = null)
    {
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(metrics);
        List<string> rows = [];
        powerStatus ??= RtssOsdPowerStatus.Empty;
        var tdpRow = Row(TdpEntry(powerStatus, true));
        var autoTdpRow = Row(AutoTdpEntry(powerStatus, true));
        if (tdpRow.Length > 0)
        {
            rows.Add(tdpRow);
        }

        if (autoTdpRow.Length > 0)
        {
            rows.Add(autoTdpRow);
        }

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var name in custom.Order)
        {
            // HC's CustomStrategy shows the widget's literal name, FPS included.
            var entry = name switch
            {
                "TIME" when custom.Time > 0 =>
                    Entry("TIME", "FFFFFF", true, TimeElements(custom.Time == 2)),
                "GPU" when custom.Gpu > 0 =>
                    Entry("GPU", GpuColor, true, GpuElements(metrics, custom.Gpu == 2)),
                "CPU" when custom.Cpu > 0 =>
                    Entry("CPU", CpuColor, true, CpuElements(metrics, custom.Cpu == 2)),
                "VRAM" when custom.Vram > 0 =>
                    Entry("VRAM", VramColor, true, VramElements(metrics, custom.Vram == 2)),
                "RAM" when custom.Ram > 0 =>
                    Entry("RAM", RamColor, true, RamElements(metrics, custom.Ram == 2)),
                "BATT" when custom.Battery > 0 =>
                    Entry("BATT", BattColor, true, BatteryElements(metrics, custom.Battery == 2)),
                "FPS" when custom.Fps > 0 =>
                    Entry("FPS", FpsColor, true, FpsElements(custom.Fps == 2)),
                _ => null
            };
            var row = Row(entry);
            if (row.Length > 0)
            {
                rows.Add(row);
            }
        }

        return Compose([.. rows]);
    }

    private static string? TdpEntry(RtssOsdPowerStatus status, bool indent)
    {
        List<string> elements = [];
        if (status.TdpWatts is { } sustained)
        {
            // Sustained, then whichever boost limits the device publishes: 25/37W on PL1/PL2 hardware.
            var value = Format(sustained, "W");
            if (status.SlowWatts is { } slow)
            {
                value += "/" + Format(slow, "W");
            }
            if (status.FastWatts is { } fast)
            {
                value += "/" + Format(fast, "W");
            }
            elements.Add(Element(value, "W"));
        }
        return Entry("TDP", TdpColor, indent, elements);
    }

    private static string? AutoTdpEntry(RtssOsdPowerStatus status, bool indent)
    {
        if (!status.AutoTdpEnabled || !status.AutoTdpRunning)
        {
            return null;
        }

        List<string> elements = [];
        AddIfNotNull(elements, status.AutoTdpWatts, "W");
        if (!string.IsNullOrWhiteSpace(status.AutoTdpActivity))
        {
            elements.Add(Element(status.AutoTdpActivity.ToUpperInvariant(), string.Empty));
        }

        return Entry("AUTO TDP", AutoTdpColor, indent, elements);
    }

    private static List<string> TimeElements(bool full)
    {
        return [Element(DateTime.Now.ToString(full ? "G" : "t", CultureInfo.InvariantCulture), string.Empty)];
    }

    private static List<string> FpsElements(bool full)
    {
        List<string> elements = [Element("<FR>", "FPS")];
        if (full)
        {
            elements.Add(Element("<FT>", "ms"));
        }

        return elements;
    }

    private static List<string> CpuElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.CpuLoadPercent, "%");
        AddIfNotNull(elements, metrics.CpuPowerWatts, "W");
        if (full)
        {
            AddIfNotNull(elements, metrics.CpuTemperatureC, "C");
        }

        return elements;
    }

    private static List<string> GpuElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.GpuLoadPercent, "%");
        AddIfNotNull(elements, metrics.GpuPowerWatts, "W");
        if (full)
        {
            AddIfNotNull(elements, metrics.GpuTemperatureC, "C");
        }

        return elements;
    }

    private static List<string> VramElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        if (full)
        {
            AddIfNotNull(elements, metrics.GpuMemoryUsedGb, metrics.GpuMemoryTotalGb, "GB");
        }
        else
        {
            AddIfNotNull(elements, metrics.GpuMemoryUsedGb, "GB");
        }

        return elements;
    }

    private static List<string> RamElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        if (full)
        {
            AddIfNotNull(elements, metrics.MemoryUsedGb, metrics.MemoryTotalGb, "GB");
        }
        else
        {
            AddIfNotNull(elements, metrics.MemoryUsedGb, "GB");
        }

        return elements;
    }

    private static List<string> BatteryElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.BatteryPercent, "%");
        if (full)
        {
            AddIfNotNull(elements, metrics.BatteryWatts, "W");
        }

        // Remaining time only while discharging, like HC: a charger makes the estimate noise.
        if (elements.Count <= 0
            || metrics is not { OnAcPower: false, BatteryMinutesRemaining: { } minutes })
        {
            return elements;
        }

        var (hours, remainder) = Math.DivRem(minutes, 60);
        elements.Add(Element(Format(hours, "h"), "h"));
        elements.Add(Element(Format(remainder, "min"), "min"));
        return elements;
    }

    private static void AddIfNotNull(List<string> elements, double? value, string unit)
    {
        if (value is { } present)
        {
            elements.Add(Element(Format(present, unit), unit));
        }
    }

    private static void AddIfNotNull(
        List<string> elements, double? value, double? available, string unit)
    {
        if (value is { } present && available is { } total)
        {
            elements.Add(Element($"{Format(present, unit)}/{Format(total, unit)}", unit));
        }
    }

    private static string Compose(params string[] rows)
    {
        List<string> populated = [];
        foreach (var row in rows)
        {
            if (row.Length > 0)
            {
                populated.Add(populated.Count == 0 ? Header + row : row);
            }
        }

        return string.Join("\n", populated);
    }

    private static string Row(params string?[] entries)
    {
        return string.Join("<C1> | <C>", entries.Where(static entry => !string.IsNullOrEmpty(entry)));
    }

    private static string? Entry(string name, string color, bool indent, List<string> elements)
    {
        if (elements.Count == 0)
        {
            return null;
        }

        var label = $"<C={color}>{name}{(indent ? "\t" : string.Empty)}<C>";
        return $"{label} {string.Join(" ", elements)}";
    }

    private static string Element(string value, string unit)
    {
        return $"<C0>{value}<S1>{unit}<S><C>";
    }

    private static string Format(double value, string unit)
    {
        var format = unit switch
        {
            "GB" => "0.0",
            "W" or "%" or "C" or "h" or "min" => "00",
            "MB" => "0",
            _ => "0.##"
        };
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
