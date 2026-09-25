using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Wizard;

internal static partial class LabSystemDump
{
    private static LabSystemDumpSectionResult CollectCpu(LabSystemDumpContext context)
    {
        List<string> issues = [];
        var token = context.Cancellation;

        // ProcessorId and any serial or unique ID are left out on purpose.
        var processors = WmiSection("root\\CIMV2", "Win32_Processor",
        [
            "Name", "Manufacturer", "Description", "Family", "Architecture", "NumberOfCores",
            "NumberOfEnabledCore", "NumberOfLogicalProcessors", "ThreadCount", "MaxClockSpeed", "CurrentClockSpeed",
            "L2CacheSize", "L3CacheSize", "SocketDesignation", "VirtualizationFirmwareEnabled"
        ], issues, token);
        var caches = WmiSection("root\\CIMV2", "Win32_CacheMemory",
            ["Purpose", "Level", "InstalledSize", "Associativity", "CacheType", "LineSize"], issues, token);
        var memory = WmiSection("root\\CIMV2", "Win32_PhysicalMemory",
            [
                "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType", "FormFactor", "DataWidth",
                "Manufacturer"
            ],
            issues, token);
        var graphics = WmiSection("root\\CIMV2", "Win32_VideoController",
        [
            "Name", "PNPDeviceID", "AdapterCompatibility", "VideoProcessor", "DriverVersion", "DriverDate",
            "AdapterRAM", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate",
            "MaxRefreshRate", "MinRefreshRate"
        ], issues, token);
        var system = WmiSection("root\\CIMV2", "Win32_OperatingSystem",
            ["Caption", "Version", "BuildNumber", "OSArchitecture", "OperatingSystemSKU", "Locale"], issues, token);

        string? identity = null;
        if (processors is List<Dictionary<string, object?>> { Count: > 0 } rows
            && rows[0].GetValueOrDefault("Description") is string description
            && CpuIdentity().Match(description) is { Success: true } match)
        {
            identity = new ProcessorInventory
            {
                Family = int.Parse(match.Groups["family"].Value, CultureInfo.InvariantCulture),
                Model = int.Parse(match.Groups["model"].Value, CultureInfo.InvariantCulture),
                Stepping = int.Parse(match.Groups["stepping"].Value, CultureInfo.InvariantCulture)
            }.NormalizedIdentity;
        }

        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var hasStatus = GlobalMemoryStatusEx(ref status);
        var installed = GetPhysicallyInstalledSystemMemory(out var installedKilobytes);
        context.Write("cpu", new
        {
            Identity = identity,
            Environment.ProcessorCount,
            Processors = processors,
            Caches = caches,
            Memory = new
            {
                TotalPhysicalBytes = hasStatus ? status.TotalPhysical : (ulong?)null,
                AvailablePhysicalBytes = hasStatus ? status.AvailablePhysical : (ulong?)null,
                InstalledBytes = installed ? installedKilobytes * 1024 : (ulong?)null,
                Modules = memory
            },
            Graphics = graphics,
            OperatingSystem = system,
            Issues = issues
        });
        var count = processors is List<Dictionary<string, object?>> list ? list.Count : 0;
        return Result("cpu", count,
            identity is null ? Plural(count, "processor", "processors") : $"processor {identity}", issues);
    }

    [GeneratedRegex(@"Family (?<family>\d+) Model (?<model>\d+) Stepping (?<stepping>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CpuIdentity();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
