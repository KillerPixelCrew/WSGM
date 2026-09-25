using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Wizard;

internal static partial class LabSystemDump
{
    private const uint AccessScheme = 16;
    private const int SystemPowerCapabilities = 4;

    /// <summary>Whether Windows reports S0 low-power idle support.</summary>
    internal static bool SupportsModernStandby()
    {
        var buffer = new byte[PowerCapabilitiesBytes];
        return CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0
               && buffer[20] != 0;
    }
    private const int PowerCapabilitiesBytes = 76;

    // Windows' power mode slider values: no overlay means the balanced position.
    private static readonly Dictionary<Guid, string> PowerOverlays = new()
    {
        [Guid.Empty] = "balanced",
        [new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")] = "best power efficiency",
        [new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f")] = "better battery",
        [new Guid("ded574b5-45a0-4f42-8737-46345c09c238")] = "best performance"
    };

    private static LabSystemDumpSectionResult CollectBatteryPower(LabSystemDumpContext context)
    {
        List<string> issues = [];
        var token = context.Cancellation;

        // Serial numbers, unique IDs and the battery's DeviceID (often the serial) are never copied.
        var win32Battery = WmiSection("root\\CIMV2", "Win32_Battery",
        [
            "Name", "Caption", "Status", "Availability", "BatteryStatus", "Chemistry", "DesignVoltage",
            "EstimatedChargeRemaining", "EstimatedRunTime", "PNPDeviceID", "PowerManagementSupported"
        ], issues, token);
        var staticData = WmiSection("root\\wmi", "BatteryStaticData",
        [
            "InstanceName", "Active", "Capabilities", "Chemistry", "Technology", "DesignedCapacity",
            "DefaultAlert1", "DefaultAlert2", "CriticalBias", "CycleCount", "Granularity1", "Granularity2",
            "Granularity3", "Granularity4", "ManufactureName", "DeviceName", "ManufactureDate"
        ], issues, token);
        var fullCharge = WmiSection("root\\wmi", "BatteryFullChargedCapacity",
            ["InstanceName", "Active", "FullChargedCapacity"], issues, token);
        var status = WmiSection("root\\wmi", "BatteryStatus",
        [
            "InstanceName", "Active", "PowerOnline", "Charging", "Discharging", "Critical", "ChargeRate",
            "DischargeRate", "RemainingCapacity", "Voltage"
        ], issues, token);
        var cycles = WmiSection("root\\wmi", "BatteryCycleCount", ["InstanceName", "Active", "CycleCount"], issues,
            token);
        var thermal = WmiSection("root\\wmi", "MSAcpi_ThermalZoneTemperature",
        [
            "InstanceName", "Active", "CurrentTemperature", "CriticalTripPoint", "PassiveTripPoint",
            "ThermalStamp", "SamplingPeriod", "ActiveTripPointCount", "ActiveTripPoint"
        ], issues, token);

        // Fan RPM, temperatures and fan-control duty from LibreHardwareMonitor: read once, never set.
        var sensors = LabLhmSensors.ReadOnce(token);
        if (sensors.Problem is { } sensorProblem)
        {
            AddIssue(issues, $"LibreHardwareMonitor: {sensorProblem}");
        }

        object schemes;
        try
        {
            schemes = PowerSchemes();
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            AddIssue(issues, $"Power plans: {ex.Message}");
            schemes = new { Problem = ex.Message };
        }

        context.Write("battery-power", new
        {
            Win32Battery = win32Battery,
            BatteryStaticData = staticData,
            BatteryFullChargedCapacity = fullCharge,
            BatteryStatus = status,
            BatteryCycleCount = cycles,
            ThermalZones = thermal,
            ThermalZoneNote = "Temperatures are in tenths of a kelvin; subtract 2732 and divide by 10 for Celsius.",
            LibreHardwareMonitor = sensors,
            PowerStatus = PowerStatus(),
            PowerCapabilities = PowerCapabilities(),
            PowerPlans = schemes,
            ChargeLimit = new
            {
                Signals = ChargeLimitSignals(context.WmiClasses),
                Note = "Hints from the vendor interfaces present; the Power step tests the limit itself."
            },
            Issues = issues
        });
        var batteries = win32Battery is List<Dictionary<string, object?>> rows ? rows.Count : 0;
        return Result("battery-power", batteries,
            batteries == 0 ? "no battery found" : Plural(batteries, "battery", "batteries"), issues);
    }

    /// <summary>Lists the vendor interfaces present that carry a charge limit on known devices.</summary>
    /// <param name="classes">The <c>root\wmi</c> classes.</param>
    /// <returns>One line per interface found; empty when none is visible.</returns>
    public static List<string> ChargeLimitSignals(IReadOnlyList<LabWmiClass> classes)
    {
        List<string> signals = [];
        if (Has("MSI_ACPI"))
        {
            signals.Add("MSI_ACPI is present (MSI devices set the charge limit through it).");
        }

        if (Has("AsusAtkWmi_WMNB"))
        {
            signals.Add("AsusAtkWmi_WMNB is present (ASUS devices set the charge limit through ATKACPI).");
        }

        if (classes.Any(entry => entry.Name.StartsWith("LENOVO_", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("LENOVO_* classes are present (Lenovo devices expose charge settings through them).");
        }

        return signals;

        bool Has(string name)
        {
            return classes.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static object PowerSchemes()
    {
        List<object> plans = [];
        var buffer = new byte[16];
        for (uint index = 0; index < 64; index++)
        {
            var size = (uint)buffer.Length;
            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
            if (result != 0)
            {
                break;
            }

            var scheme = new Guid(buffer);
            plans.Add(new { Guid = scheme.ToString("D"), Name = SchemeName(scheme) });
        }

        string? active = null;
        if (PowerGetActiveScheme(IntPtr.Zero, out var activePointer) == 0 && activePointer != IntPtr.Zero)
        {
            try
            {
                active = Marshal.PtrToStructure<Guid>(activePointer).ToString("D");
            }
            finally
            {
                LocalFree(activePointer);
            }
        }

        return new
        {
            Active = active,
            Plans = plans,
            Overlay = Overlay(PowerGetEffectiveOverlayScheme),
            ConfiguredOverlay = Overlay(PowerGetActualOverlayScheme)
        };

        static object? Overlay(OverlayReader read)
        {
            try
            {
                if (read(out var overlay) != 0)
                {
                    return null;
                }

                return new { Guid = overlay.ToString("D"), Name = PowerOverlays.GetValueOrDefault(overlay) };
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    private static string? SchemeName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0
            || size == 0
            || size > 4096)
        {
            return null;
        }

        var buffer = new byte[size];
        return PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    private static object? PowerStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return null;
        }

        return new
        {
            AcLine = status.AcLineStatus switch { 0 => "offline", 1 => "online", _ => "unknown" },
            BatteryFlag = Hex(status.BatteryFlag, 2),
            BatteryPercent = status.BatteryLifePercent == 255 ? (int?)null : status.BatteryLifePercent,
            BatterySaver = status.SystemStatusFlag == 1
        };
    }

    private static object? PowerCapabilities()
    {
        var buffer = new byte[PowerCapabilitiesBytes];
        if (CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, buffer, (uint)buffer.Length) != 0)
        {
            return null;
        }

        return new
        {
            PowerButton = buffer[0] != 0,
            SleepButton = buffer[1] != 0,
            Lid = buffer[2] != 0,
            S1 = buffer[3] != 0,
            S2 = buffer[4] != 0,
            S3 = buffer[5] != 0,
            S4 = buffer[6] != 0,
            S5 = buffer[7] != 0,
            HibernationFile = buffer[8] != 0,
            ThermalControl = buffer[13] != 0,
            ProcessorThrottle = buffer[14] != 0,
            ModernStandby = buffer[20] != 0,
            ModernStandbyConnectivity = buffer[23] != 0,
            BatteriesPresent = buffer[30] != 0,
            BatteriesShortTerm = buffer[31] != 0
        };
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        IntPtr rootKey,
        IntPtr schemeGuid,
        IntPtr subgroupGuid,
        uint accessFlags,
        uint index,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(
        IntPtr rootKey,
        in Guid schemeGuid,
        IntPtr subgroupGuid,
        IntPtr settingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr activeScheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActualOverlayScheme(out Guid overlay);

    [LibraryImport("powrprof.dll")]
    private static partial uint CallNtPowerInformation(
        int level,
        IntPtr input,
        uint inputLength,
        [Out] byte[] output,
        uint outputLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    private delegate uint OverlayReader(out Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
