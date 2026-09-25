using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Battery and charger state.</summary>
internal sealed record LabBatteryReading
{
    /// <summary>Whether the charger is connected; null when Windows does not know.</summary>
    public bool? AcOnline { get; init; }

    /// <summary>Battery percentage.</summary>
    public int? Percent { get; init; }

    /// <summary>Charge rate in milliwatts.</summary>
    public int? ChargeRateMilliwatts { get; init; }

    /// <summary>Discharge rate in milliwatts, which is the whole device's draw on battery.</summary>
    public int? DischargeRateMilliwatts { get; init; }

    /// <summary>Remaining capacity in milliwatt hours.</summary>
    public int? RemainingMilliwattHours { get; init; }

    /// <summary>Battery voltage in millivolts.</summary>
    public int? VoltageMillivolts { get; init; }

    /// <summary>Whether the battery reports charging.</summary>
    public bool? Charging { get; init; }

    /// <summary>Whether the battery reports discharging.</summary>
    public bool? Discharging { get; init; }
}

/// <summary>One ACPI thermal zone.</summary>
/// <param name="Name">Zone name.</param>
/// <param name="Celsius">Temperature.</param>
/// <param name="Source">Where it was read.</param>
internal sealed record LabThermalZone(string Name, double Celsius, string Source);

/// <summary>Processor clocks.</summary>
internal sealed record LabCpuReading
{
    /// <summary>Logical processors reported.</summary>
    public int Processors { get; init; }

    /// <summary>Maximum MHz, from CallNtPowerInformation.</summary>
    public int? MaxMhz { get; init; }

    /// <summary>Average current MHz, from CallNtPowerInformation. Often the base clock on newer Windows.</summary>
    public int? CurrentMhz { get; init; }

    /// <summary>Average MHz limit, from CallNtPowerInformation.</summary>
    public int? LimitMhz { get; init; }

    /// <summary>Processor performance percentage, from the Processor Information counters.</summary>
    public double? PerformancePercent { get; init; }

    /// <summary>Processor utility percentage.</summary>
    public double? UtilityPercent { get; init; }

    /// <summary>Base frequency the performance percentage refers to.</summary>
    public int? BaseMhz { get; init; }

    /// <summary>Effective MHz: base frequency times performance.</summary>
    public int? EffectiveMhz { get; init; }
}

/// <summary>The active Windows power plan.</summary>
/// <param name="Scheme">Plan GUID.</param>
/// <param name="Name">Plan name.</param>
/// <param name="Overlay">Power mode overlay GUID.</param>
internal sealed record LabPowerScheme(string? Scheme, string? Name, string? Overlay);

/// <summary>A Windows power or energy meter.</summary>
/// <param name="Name">Meter instance.</param>
/// <param name="Milliwatts">Power reading.</param>
/// <param name="Source">Counter set.</param>
internal sealed record LabPowerMeter(string Name, long Milliwatts, string Source);

/// <summary>Everything the power stage reads at one moment. Nothing is written.</summary>
internal sealed record LabPowerSample
{
    /// <summary>When it was read.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What was happening, for example <c>before-load</c>.</summary>
    public required string Label { get; init; }

    /// <summary>Battery and charger.</summary>
    public LabBatteryReading Battery { get; init; } = new();

    /// <summary>Thermal zones.</summary>
    public IReadOnlyList<LabThermalZone> Thermal { get; init; } = [];

    /// <summary>Processor clocks.</summary>
    public LabCpuReading Cpu { get; init; } = new();

    /// <summary>Active power plan.</summary>
    public LabPowerScheme? Scheme { get; init; }

    /// <summary>Fan speeds, when the device has a readable source.</summary>
    public IReadOnlyList<LabFanReading> Fans { get; init; } = [];

    /// <summary>Power and energy meters Windows exposes.</summary>
    public IReadOnlyList<LabPowerMeter> Meters { get; init; } = [];

    /// <summary>Readings that could not be taken, and why.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];
}

/// <summary>Reads battery, thermal, clock, power plan and fan telemetry.</summary>
internal static class LabPowerTelemetry
{
    /// <summary>Reads everything at once.</summary>
    /// <param name="label">What is happening.</param>
    /// <param name="fans">Reads fan speeds from the device transport, or null.</param>
    /// <returns>The sample.</returns>
    public static LabPowerSample Read(string label, Func<IReadOnlyList<LabFanReading>>? fans)
    {
        List<string> unavailable = [];
        IReadOnlyList<LabFanReading> fanReadings = [];
        if (fans is not null)
        {
            try
            {
                fanReadings = fans();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                unavailable.Add($"fans: {ex.Message}");
            }
        }

        return new LabPowerSample
        {
            At = DateTimeOffset.UtcNow,
            Label = label,
            Battery = Battery(unavailable),
            Thermal = Thermal(unavailable),
            Cpu = Cpu(unavailable),
            Scheme = Scheme(unavailable),
            Fans = fanReadings,
            Meters = Meters(unavailable),
            Unavailable = unavailable
        };
    }

    /// <summary>The charger state from GetSystemPowerStatus: 0 offline, 1 online, null unknown.</summary>
    /// <returns>The state.</returns>
    public static int? AcLine()
    {
        return GetSystemPowerStatus(out var status) && status.AcLine is 0 or 1 ? status.AcLine : null;
    }

    /// <summary>Battery percentage from GetSystemPowerStatus, or null.</summary>
    /// <returns>Percent.</returns>
    public static int? BatteryPercent()
    {
        return GetSystemPowerStatus(out var status) && status.Percent <= 100 ? status.Percent : null;
    }

    /// <summary>One line for the page, for example <c>2.9 GHz, 71 °C, drawing 21.4 W</c>.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns>The line.</returns>
    public static string Describe(LabPowerSample sample)
    {
        List<string> parts = [];
        if (sample.Cpu.EffectiveMhz is { } mhz and > 0)
        {
            parts.Add($"processor at {mhz / 1000.0:0.0} GHz");
        }

        if (sample.Thermal.Count > 0)
        {
            parts.Add($"hottest sensor {sample.Thermal.Max(zone => zone.Celsius):0} °C");
        }

        if (sample.Battery.DischargeRateMilliwatts is { } discharge and > 0)
        {
            parts.Add($"battery draining {discharge / 1000.0:0.0} W");
        }
        else if (sample.Battery.ChargeRateMilliwatts is { } charge and > 0)
        {
            parts.Add($"charging at {charge / 1000.0:0.0} W");
        }

        foreach (var fan in sample.Fans)
        {
            parts.Add(fan.Unit == "rpm" ? $"{fan.Name.ToLowerInvariant()} {fan.Value} rpm" : $"{fan.Name.ToLowerInvariant()} reading {fan.Value}");
        }

        return parts.Count == 0 ? "no readings available" : string.Join(", ", parts);
    }

    private static LabBatteryReading Battery(List<string> unavailable)
    {
        bool? ac = null;
        int? percent = null;
        if (GetSystemPowerStatus(out var status))
        {
            ac = status.AcLine switch { 0 => false, 1 => true, _ => null };
            percent = status.Percent <= 100 ? status.Percent : null;
        }

        var reading = new LabBatteryReading { AcOnline = ac, Percent = percent };
        try
        {
            foreach (var row in Query("root\\WMI",
                         "SELECT PowerOnline, ChargeRate, DischargeRate, RemainingCapacity, Voltage, Charging, Discharging FROM BatteryStatus"))
            {
                return reading with
                {
                    AcOnline = Bool(row, "PowerOnline") ?? ac,
                    ChargeRateMilliwatts = Int(row, "ChargeRate"),
                    DischargeRateMilliwatts = Int(row, "DischargeRate"),
                    RemainingMilliwattHours = Int(row, "RemainingCapacity"),
                    VoltageMillivolts = Int(row, "Voltage"),
                    Charging = Bool(row, "Charging"),
                    Discharging = Bool(row, "Discharging")
                };
            }

            unavailable.Add("battery rate: no BatteryStatus instance");
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            unavailable.Add($"battery rate: {ex.Message}");
        }

        return reading;
    }

    private static IReadOnlyList<LabThermalZone> Thermal(List<string> unavailable)
    {
        List<LabThermalZone> zones = [];
        try
        {
            foreach (var row in Query("root\\WMI",
                         "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
            {
                if (Int(row, "CurrentTemperature") is { } tenthsKelvin and > 0)
                {
                    zones.Add(new LabThermalZone(Text(row, "InstanceName") ?? "zone", (tenthsKelvin / 10.0) - 273.15,
                        "MSAcpi_ThermalZoneTemperature"));
                }
            }
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            unavailable.Add($"ACPI thermal zones: {ex.Message}");
        }

        try
        {
            foreach (var row in Query("root\\CIMV2",
                         "SELECT Name, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            {
                if (Int(row, "HighPrecisionTemperature") is { } tenthsKelvin and > 0)
                {
                    zones.Add(new LabThermalZone(Text(row, "Name") ?? "zone", (tenthsKelvin / 10.0) - 273.15,
                        "ThermalZoneInformation"));
                }
            }
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            unavailable.Add($"thermal zone counters: {ex.Message}");
        }

        return [.. zones.Select(zone => zone with { Celsius = Math.Round(zone.Celsius, 1) })];
    }

    private static LabCpuReading Cpu(List<string> unavailable)
    {
        var count = Environment.ProcessorCount;
        var reading = new LabCpuReading { Processors = count };
        var size = Marshal.SizeOf<ProcessorPowerInformation>();
        var buffer = new ProcessorPowerInformation[count];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = CallNtPowerInformation(11, IntPtr.Zero, 0, handle.AddrOfPinnedObject(), (uint)(size * count));
            if (status == 0)
            {
                reading = reading with
                {
                    MaxMhz = (int)buffer.Max(item => item.MaxMhz),
                    CurrentMhz = (int)buffer.Average(item => item.CurrentMhz),
                    LimitMhz = (int)buffer.Average(item => item.MhzLimit)
                };
            }
            else
            {
                unavailable.Add($"processor information: status 0x{status:X8}");
            }
        }
        finally
        {
            handle.Free();
        }

        try
        {
            foreach (var row in Query("root\\CIMV2",
                         "SELECT PercentProcessorPerformance, PercentProcessorUtility, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name = '_Total'"))
            {
                var performance = Int(row, "PercentProcessorPerformance");
                var baseMhz = Int(row, "ProcessorFrequency");
                reading = reading with
                {
                    PerformancePercent = performance,
                    UtilityPercent = Int(row, "PercentProcessorUtility"),
                    BaseMhz = baseMhz,
                    EffectiveMhz = performance is { } p && baseMhz is { } b ? (int)((long)p * b / 100) : null
                };
            }
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            unavailable.Add($"processor counters: {ex.Message}");
        }

        return reading;
    }

    private static LabPowerScheme? Scheme(List<string> unavailable)
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var active) != 0)
            {
                unavailable.Add("power plan: PowerGetActiveScheme failed");
                return null;
            }

            Guid scheme;
            try
            {
                scheme = Marshal.PtrToStructure<Guid>(active);
            }
            finally
            {
                LocalFree(active);
            }

            string? name = null;
            uint bytes = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref bytes) == 0
                && bytes is > 0 and < 4096)
            {
                var text = new byte[bytes];
                if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, text, ref bytes) == 0)
                {
                    name = System.Text.Encoding.Unicode.GetString(text).TrimEnd('\0');
                }
            }

            string? overlay = null;
            try
            {
                if (PowerGetEffectiveOverlayScheme(out var mode) == 0)
                {
                    overlay = mode.ToString();
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows versions without power mode overlays.
            }

            return new LabPowerScheme(scheme.ToString(), name, overlay);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            unavailable.Add($"power plan: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<LabPowerMeter> Meters(List<string> unavailable)
    {
        List<LabPowerMeter> meters = [];
        foreach (var (cls, source) in new[]
                 {
                     ("Win32_PerfFormattedData_Counters_PowerMeter", "Power Meter"),
                     ("Win32_PerfFormattedData_Counters_EnergyMeter", "Energy Meter")
                 })
        {
            try
            {
                foreach (var row in Query("root\\CIMV2", $"SELECT Name, Power FROM {cls}"))
                {
                    if (Long(row, "Power") is { } power)
                    {
                        meters.Add(new LabPowerMeter(Text(row, "Name") ?? "meter", power, source));
                    }
                }
            }
            catch (Exception ex) when (IsWmiFailure(ex))
            {
                unavailable.Add($"{source}: {ex.Message}");
            }
        }

        return meters;
    }

    private static bool IsWmiFailure(Exception ex)
    {
        return ex is ManagementException or UnauthorizedAccessException or COMException;
    }

    private static List<Dictionary<string, object?>> Query(string scope, string query)
    {
        List<Dictionary<string, object?>> rows = [];
        using ManagementObjectSearcher searcher = new(scope, query);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                rows.Add(item.Properties.Cast<PropertyData>().ToDictionary(property => property.Name,
                    property => (object?)property.Value, StringComparer.OrdinalIgnoreCase));
            }
        }

        return rows;
    }

    private static string? Text(Dictionary<string, object?> row, string name)
    {
        return Convert.ToString(row.GetValueOrDefault(name), CultureInfo.InvariantCulture)?.Trim();
    }

    private static int? Int(Dictionary<string, object?> row, string name)
    {
        return Long(row, name) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    private static long? Long(Dictionary<string, object?> row, string name)
    {
        return row.GetValueOrDefault(name) is { } value
               && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static bool? Bool(Dictionary<string, object?> row, string name)
    {
        return row.GetValueOrDefault(name) is bool value ? value : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLine;
        public byte Flags;
        public byte Percent;
        public byte Saver;
        public uint RemainingSeconds;
        public uint FullSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputBytes, IntPtr output,
        uint outputBytes);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting,
        byte[]? buffer, ref uint bytes);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
