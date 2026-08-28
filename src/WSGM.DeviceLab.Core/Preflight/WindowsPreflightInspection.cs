using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceLab.Core.Preflight;

/// <summary>Closed kinds of external components inspected during Stage 0.</summary>
public enum DeviceLabExternalComponentKind
{
    /// <summary>A running process with an exact executable name.</summary>
    Process,

    /// <summary>An installed Windows service.</summary>
    Service,

    /// <summary>An installed kernel or filesystem driver service.</summary>
    Driver,

    /// <summary>A WMI namespace and class definition.</summary>
    WmiProvider,

    /// <summary>A native library at an exact path.</summary>
    NativeLibrary,

    /// <summary>A reviewed helper at an exact path.</summary>
    Helper,

    /// <summary>An installed Task Scheduler task.</summary>
    ScheduledTask,

    /// <summary>An installed Windows Event Log source.</summary>
    EventSource,
}

/// <summary>Catalog-owned descriptor for one relevant external component.</summary>
public sealed record DeviceLabExternalComponentDescriptor
{
    /// <summary>Stable descriptor identifier.</summary>
    public required string ComponentId { get; init; }

    /// <summary>How the component is discovered.</summary>
    public required DeviceLabExternalComponentKind Kind { get; init; }

    /// <summary>Exact process/service/driver/source name or absolute file path.</summary>
    public required string Selector { get; init; }

    /// <summary>WMI namespace for a provider descriptor.</summary>
    public string? Namespace { get; init; }

    /// <summary>Resource this component may conflict with, without claiming that it does.</summary>
    public string? ResourceId { get; init; }
}

/// <summary>Read-only diagnostics client supplied by the production device owner.</summary>
public interface IDeviceOwnerDiagnosticsSource
{
    /// <summary>Attempts to receive one bounded versioned snapshot.</summary>
    /// <param name="snapshot">Snapshot when the owner answered successfully.</param>
    /// <returns><see langword="true"/> when a complete snapshot was received.</returns>
    bool TryRead(out DeviceDiagnosticsSnapshot? snapshot);
}

/// <summary>Result of finding the session owner and asking it for diagnostics.</summary>
public sealed record DeviceLabOwnerInspection
{
    /// <summary>Owner discovery outcome.</summary>
    public required DeviceOwnerDiscoveryState State { get; init; }

    /// <summary>Versioned snapshot supplied by the owner.</summary>
    public DeviceDiagnosticsSnapshot? Snapshot { get; init; }

    /// <summary>Whether Device Integration could be derived from the owner state.</summary>
    public bool? DeviceIntegrationEnabled { get; init; }

    /// <summary>Bounded diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Finds the session owner without starting, stopping, or taking it over.</summary>
public static class DeviceLabOwnerInspector
{
    /// <summary>Builds the session-scoped production owner object name.</summary>
    /// <param name="sessionId">Windows session identifier.</param>
    /// <returns>Exact local-session object name.</returns>
    public static string OwnerObjectName(int sessionId) => $@"Local\WSGM.DeviceOwner.{sessionId}";

    /// <summary>Inspects the current session owner and optional read-only diagnostics source.</summary>
    /// <param name="diagnosticsSource">Owner diagnostics transport; never a raw device transport.</param>
    /// <returns>Owner state and generation/resource snapshot when available.</returns>
    public static DeviceLabOwnerInspection Inspect(IDeviceOwnerDiagnosticsSource? diagnosticsSource = null)
    {
        int sessionId;
        using (Process current = Process.GetCurrentProcess())
        {
            sessionId = current.SessionId;
        }

        string objectName = OwnerObjectName(sessionId);

        bool ownerPresent;
        try
        {
            ownerPresent = Mutex.TryOpenExisting(objectName, out Mutex? owner);
            owner?.Dispose();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or WaitHandleCannotBeOpenedException)
        {
            return new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.Ambiguous,
                Detail = exception.GetType().Name,
            };
        }

        return Classify(ownerPresent, diagnosticsSource);
    }

    /// <summary>Purely classifies owner-object and diagnostics evidence.</summary>
    /// <param name="ownerPresent">Whether the session object exists.</param>
    /// <param name="diagnosticsSource">Optional bounded diagnostics source.</param>
    /// <returns>Fail-closed owner inspection.</returns>
    public static DeviceLabOwnerInspection Classify(
        bool ownerPresent,
        IDeviceOwnerDiagnosticsSource? diagnosticsSource)
    {
        if (!ownerPresent)
        {
            return new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.Absent,
                DeviceIntegrationEnabled = null,
            };
        }

        if (diagnosticsSource is null
            || !diagnosticsSource.TryRead(out DeviceDiagnosticsSnapshot? snapshot)
            || snapshot is null)
        {
            return new DeviceLabOwnerInspection
            {
                State = DeviceOwnerDiscoveryState.PresentWithoutDiagnostics,
                DeviceIntegrationEnabled = null,
                Detail = "The owner object exists but no complete diagnostics snapshot was available.",
            };
        }

        return new DeviceLabOwnerInspection
        {
            State = DeviceOwnerDiscoveryState.PresentWithDiagnostics,
            Snapshot = snapshot,
            DeviceIntegrationEnabled = snapshot.CycleState is not DeviceCycleState.Disabled,
        };
    }
}

/// <summary>Read-only Windows collectors used to populate Stage 0 safety snapshots.</summary>
public static partial class WindowsPreflightInspection
{
    /// <summary>Observes external power, battery percentage, and accessible thermal zones.</summary>
    /// <returns>Best-effort power and thermal values; unavailable values remain null.</returns>
    public static DeviceLabPowerThermalSnapshot CollectPowerThermal()
    {
        bool? externalPower = null;
        int? batteryPercent = null;
        if (GetSystemPowerStatus(out SystemPowerStatus status) != 0)
        {
            externalPower = status.AcLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
            batteryPercent = status.BatteryLifePercent <= 100
                ? status.BatteryLifePercent
                : null;
        }

        return new DeviceLabPowerThermalSnapshot
        {
            ExternalPowerConnected = externalPower,
            BatteryPercent = batteryPercent,
            TemperatureCelsius = ReadHighestThermalZoneCelsius(),
        };
    }

    /// <summary>Detects exact catalog-named components without inferring resource ownership.</summary>
    /// <param name="descriptors">Exact catalog descriptors.</param>
    /// <returns>Observations in stable component-ID order.</returns>
    public static IReadOnlyList<DeviceLabExternalComponent> CollectExternalComponents(
        IReadOnlyList<DeviceLabExternalComponentDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        return [.. descriptors
            .OrderBy(descriptor => descriptor.ComponentId, StringComparer.Ordinal)
            .Select(InspectComponent)];
    }

    private static DeviceLabExternalComponent InspectComponent(
        DeviceLabExternalComponentDescriptor descriptor)
    {
        bool present = false;
        bool accessible = true;
        try
        {
            present = descriptor.Kind switch
            {
                DeviceLabExternalComponentKind.Process => ProcessExists(descriptor.Selector),
                DeviceLabExternalComponentKind.Service => WmiNamedObjectExists(
                    "Win32_Service", descriptor.Selector),
                DeviceLabExternalComponentKind.Driver => WmiNamedObjectExists(
                    "Win32_SystemDriver", descriptor.Selector),
                DeviceLabExternalComponentKind.WmiProvider => WmiClassExists(
                    descriptor.Namespace, descriptor.Selector),
                DeviceLabExternalComponentKind.NativeLibrary
                    or DeviceLabExternalComponentKind.Helper => File.Exists(descriptor.Selector),
                DeviceLabExternalComponentKind.ScheduledTask => TaskExists(descriptor.Selector),
                DeviceLabExternalComponentKind.EventSource => EventSourceExists(descriptor.Selector),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or IOException or SecurityException)
        {
            accessible = false;
        }

        return new DeviceLabExternalComponent
        {
            ComponentId = descriptor.ComponentId,
            Kind = descriptor.Kind.ToString(),
            Present = present,
            Accessible = accessible,
            ResourceId = descriptor.ResourceId,
            OwnershipEvidence = DeviceLabOwnershipEvidence.PresenceOnly,
        };
    }

    private static bool ProcessExists(string selector)
    {
        string processName = Path.GetFileNameWithoutExtension(selector);
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool WmiNamedObjectExists(string className, string name)
    {
        string escaped = name.Replace("'", "''", StringComparison.Ordinal);
        using ManagementObjectSearcher searcher = new(
            "root\\CIMV2",
            $"SELECT Name FROM {className} WHERE Name = '{escaped}'");
        foreach (ManagementBaseObject result in searcher.Get())
        {
            result.Dispose();
            return true;
        }

        return false;
    }

    private static bool WmiClassExists(string? wmiNamespace, string className)
    {
        if (string.IsNullOrWhiteSpace(wmiNamespace))
        {
            return false;
        }

        using ManagementClass definition = new(
            new ManagementScope(wmiNamespace),
            new ManagementPath(className),
            options: null);
        definition.Get();
        return true;
    }

    private static bool TaskExists(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)
            || Path.IsPathRooted(selector)
            || selector.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string relative = selector.TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        string tasksRoot = Path.Combine(Environment.SystemDirectory, "Tasks");
        string taskPath = Path.GetFullPath(Path.Combine(tasksRoot, relative));
        return taskPath.StartsWith(
                tasksRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(taskPath);
    }

    private static bool EventSourceExists(string selector)
    {
        using RegistryKey? logs = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\EventLog",
            writable: false);
        if (logs is null)
        {
            return false;
        }

        foreach (string logName in logs.GetSubKeyNames())
        {
            using RegistryKey? log = logs.OpenSubKey(logName, writable: false);
            if (log?.GetSubKeyNames().Contains(selector, StringComparer.OrdinalIgnoreCase) is true)
            {
                return true;
            }
        }

        return false;
    }

    private static double? ReadHighestThermalZoneCelsius()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            List<double> temperatures = [];
            foreach (ManagementBaseObject result in searcher.Get())
            {
                using (result)
                {
                    if (result["CurrentTemperature"] is not null
                        && double.TryParse(
                            result["CurrentTemperature"].ToString(),
                            out double deciKelvin))
                    {
                        temperatures.Add((deciKelvin / 10.0) - 273.15);
                    }
                }
            }

            return temperatures.Count == 0 ? null : temperatures.Max();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    private static partial int GetSystemPowerStatus(out SystemPowerStatus status);

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
