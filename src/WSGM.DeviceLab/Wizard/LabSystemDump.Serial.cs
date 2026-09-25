using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One serial (COM) port, listed without opening it.</summary>
internal sealed record LabSerialPort
{
    /// <summary>Device instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Port name, for example COM3.</summary>
    public string? PortName { get; init; }

    /// <summary>Friendly name.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>USB vendor ID from this device or its nearest USB parent.</summary>
    public string? VendorId { get; init; }

    /// <summary>USB product ID from this device or its nearest USB parent.</summary>
    public string? ProductId { get; init; }

    /// <summary>Hardware IDs.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Driver service.</summary>
    public string? Service { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Parent instance ID.</summary>
    public string? Parent { get; init; }
}

internal static partial class LabSystemDump
{
    private static LabSystemDumpSectionResult CollectSerialPorts(LabSystemDumpContext context)
    {
        List<string> issues = [];
        Dictionary<string, LabDumpDevice> byId = new(StringComparer.OrdinalIgnoreCase);
        foreach (var device in context.Devices)
        {
            byId.TryAdd(device.InstanceId, device);
        }

        List<LabSerialPort> ports = [];
        foreach (var device in context.Devices)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var portName = PortName(device.InstanceId, issues);
            if (!string.Equals(device.Class, "Ports", StringComparison.OrdinalIgnoreCase) && portName is null)
            {
                continue;
            }

            var (vendor, product) = UsbIds(device, byId);
            ports.Add(new LabSerialPort
            {
                InstanceId = device.InstanceId,
                PortName = portName,
                FriendlyName = device.FriendlyName ?? device.Description,
                VendorId = vendor,
                ProductId = product,
                HardwareIds = device.HardwareIds,
                Service = device.Service,
                DriverProvider = device.DriverProvider,
                DriverVersion = device.DriverVersion,
                Parent = device.Parent
            });
        }

        // SERIALCOMM also names ports that have no PnP node, such as some legacy UARTs.
        Dictionary<string, string> serialComm = new(StringComparer.Ordinal);
        try
        {
            using var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            foreach (var name in map?.GetValueNames() ?? [])
            {
                if (map!.GetValue(name) is string port)
                {
                    serialComm[name] = port;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SecurityException)
        {
            AddIssue(issues, $"SERIALCOMM: {ex.Message}");
        }

        if (context.Devices.Count == 0)
        {
            AddIssue(issues, "The device list was not read, so only SERIALCOMM is listed.");
        }

        context.Write("serial-ports", new { Ports = ports, SerialComm = serialComm, Issues = issues });
        var count = Math.Max(ports.Count, serialComm.Count);
        return Result("serial-ports", count, count == 0 ? "none" : Plural(count, "port", "ports"), issues);
    }

    private static string? PortName(string instanceId, List<string> issues)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return key?.GetValue("PortName") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SecurityException or ArgumentException)
        {
            AddIssue(issues, $"{instanceId}: {ex.Message}");
            return null;
        }
    }

    // Walks up to the nearest node whose ID carries VID_xxxx and PID_xxxx.
    private static (string? Vendor, string? Product) UsbIds(
        LabDumpDevice device,
        Dictionary<string, LabDumpDevice> byId)
    {
        var current = device;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            foreach (var id in current.HardwareIds.Prepend(current.InstanceId))
            {
                if (VidPid().Match(id) is { Success: true } match)
                {
                    return ("0x" + match.Groups["vid"].Value.ToUpperInvariant(),
                        "0x" + match.Groups["pid"].Value.ToUpperInvariant());
                }
            }

            current = current.Parent is { } parent ? byId.GetValueOrDefault(parent) : null;
        }

        return (null, null);
    }

    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4}).*?PID_(?<pid>[0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex VidPid();
}
