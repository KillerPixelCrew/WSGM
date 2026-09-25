using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One present PnP device node.</summary>
internal sealed record LabDumpDevice
{
    /// <summary>Device instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Parent instance ID.</summary>
    public string? Parent { get; init; }

    /// <summary>Setup class name.</summary>
    public string? Class { get; init; }

    /// <summary>Setup class GUID.</summary>
    public string? ClassGuid { get; init; }

    /// <summary>Friendly name.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Device description.</summary>
    public string? Description { get; init; }

    /// <summary>Description the bus reported, before any INF renamed the device.</summary>
    public string? BusReportedDescription { get; init; }

    /// <summary>Manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Hardware IDs.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Compatible IDs.</summary>
    public IReadOnlyList<string> CompatibleIds { get; init; } = [];

    /// <summary>Function driver service.</summary>
    public string? Service { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Driver date, as yyyy-MM-dd.</summary>
    public string? DriverDate { get; init; }

    /// <summary>INF the driver was installed from.</summary>
    public string? DriverInf { get; init; }

    /// <summary>Lower and upper filter drivers.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    /// <summary>Configuration manager status flags, as hex.</summary>
    public string? Status { get; init; }

    /// <summary>Whether the device is started.</summary>
    public bool Started { get; init; }

    /// <summary>Problem code, when the device has a problem.</summary>
    public int? Problem { get; init; }

    /// <summary>Bus location text.</summary>
    public string? LocationInfo { get; init; }

    /// <summary>Location paths.</summary>
    public IReadOnlyList<string> LocationPaths { get; init; } = [];
}

internal static partial class LabSystemDump
{
    private const int MaximumDevices = 4000;
    private const int MaximumPropertyBytes = 64 * 1024;
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const int CmGetIdListFilterPresent = 0x100;
    private const uint DnStarted = 0x8;
    private const uint DnHasProblem = 0x400;
    private const uint DevPropTypeString = 0x12;
    private const uint DevPropTypeStringList = 0x2012;
    private const uint DevPropTypeGuid = 0x0D;
    private const uint DevPropTypeFileTime = 0x10;

    private static readonly Guid DeviceFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DriverFormat = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly DevPropKey DeviceDescription = new(DeviceFormat, 2);
    private static readonly DevPropKey HardwareIdsKey = new(DeviceFormat, 3);
    private static readonly DevPropKey CompatibleIdsKey = new(DeviceFormat, 4);
    private static readonly DevPropKey ServiceKey = new(DeviceFormat, 6);
    private static readonly DevPropKey ClassKey = new(DeviceFormat, 9);
    private static readonly DevPropKey ClassGuidKey = new(DeviceFormat, 10);
    private static readonly DevPropKey ManufacturerKey = new(DeviceFormat, 13);
    private static readonly DevPropKey FriendlyNameKey = new(DeviceFormat, 14);
    private static readonly DevPropKey LocationInfoKey = new(DeviceFormat, 15);
    private static readonly DevPropKey UpperFiltersKey = new(DeviceFormat, 19);
    private static readonly DevPropKey LowerFiltersKey = new(DeviceFormat, 20);
    private static readonly DevPropKey LocationPathsKey = new(DeviceFormat, 37);

    private static readonly DevPropKey BusReportedDescriptionKey =
        new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    private static readonly DevPropKey DriverDateKey = new(DriverFormat, 2);
    private static readonly DevPropKey DriverVersionKey = new(DriverFormat, 3);
    private static readonly DevPropKey DriverInfKey = new(DriverFormat, 5);
    private static readonly DevPropKey DriverProviderKey = new(DriverFormat, 9);

    private static readonly DevPropKey InterfaceInstanceIdKey =
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    private static LabSystemDumpSectionResult CollectDeviceTree(LabSystemDumpContext context)
    {
        List<string> issues = [];
        var ids = PresentDeviceIds();
        List<LabDumpDevice> devices = [];
        foreach (var id in ids.Take(MaximumDevices))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (CM_Locate_DevNodeW(out var node, id, 0) != CrSuccess)
            {
                AddIssue(issues, $"{id}: not found");
                continue;
            }

            devices.Add(ReadDevice(id, node));
        }

        if (ids.Count > MaximumDevices)
        {
            AddIssue(issues, $"Only the first {MaximumDevices} of {ids.Count} devices were read.");
        }

        context.Devices = devices;
        context.Write("device-tree", new { devices.Count, Truncated = ids.Count > MaximumDevices, Devices = devices });
        return Result("device-tree", devices.Count, Plural(devices.Count, "device", "devices"), issues);
    }

    private static List<string> PresentDeviceIds()
    {
        // The list can grow between the size and the read; retry a few times on a too-small buffer.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, null, CmGetIdListFilterPresent) != CrSuccess || length <= 0)
            {
                return [];
            }

            var buffer = new char[length];
            var result = CM_Get_Device_ID_ListW(null, buffer, length, CmGetIdListFilterPresent);
            if (result == CrSuccess)
            {
                return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            }

            if (result != CrBufferSmall)
            {
                throw new InvalidOperationException($"CM_Get_Device_ID_List failed with {result}.");
            }
        }

        throw new InvalidOperationException("The device list kept changing while it was read.");
    }

    private static LabDumpDevice ReadDevice(string id, uint node)
    {
        int? problemCode = null;
        uint? statusFlags = null;
        if (CM_Get_DevNode_Status(out var status, out var problem, node, 0) == CrSuccess)
        {
            statusFlags = status;
            problemCode = (status & DnHasProblem) != 0 ? (int)problem : null;
        }

        return new LabDumpDevice
        {
            InstanceId = id,
            Parent = ParentId(node),
            Class = NodeString(node, ClassKey),
            ClassGuid = NodeString(node, ClassGuidKey),
            FriendlyName = NodeString(node, FriendlyNameKey),
            Description = NodeString(node, DeviceDescription),
            BusReportedDescription = NodeString(node, BusReportedDescriptionKey),
            Manufacturer = NodeString(node, ManufacturerKey),
            HardwareIds = NodeStrings(node, HardwareIdsKey),
            CompatibleIds = NodeStrings(node, CompatibleIdsKey),
            Service = NodeString(node, ServiceKey),
            DriverProvider = NodeString(node, DriverProviderKey),
            DriverVersion = NodeString(node, DriverVersionKey),
            DriverDate = NodeString(node, DriverDateKey),
            DriverInf = NodeString(node, DriverInfKey),
            Filters =
            [
                .. NodeStrings(node, LowerFiltersKey).Select(filter => $"lower:{filter}"),
                .. NodeStrings(node, UpperFiltersKey).Select(filter => $"upper:{filter}")
            ],
            Status = statusFlags is { } flags ? Hex(flags) : null,
            Started = statusFlags is { } started && (started & DnStarted) != 0,
            Problem = problemCode,
            LocationInfo = NodeString(node, LocationInfoKey),
            LocationPaths = NodeStrings(node, LocationPathsKey)
        };
    }

    private static string? ParentId(uint node)
    {
        if (CM_Get_Parent(out var parent, node, 0) != CrSuccess
            || CM_Get_Device_ID_Size(out var characters, parent, 0) != CrSuccess
            || characters <= 0
            || characters > 4096)
        {
            return null;
        }

        var buffer = new char[characters + 1];
        return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == CrSuccess
            ? new string(buffer, 0, characters)
            : null;
    }

    private static string? NodeString(uint node, in DevPropKey key)
    {
        return ReadNodeProperty(node, key) is { } value
            ? DecodeProperty(value.Type, value.Bytes).FirstOrDefault()
            : null;
    }

    private static IReadOnlyList<string> NodeStrings(uint node, in DevPropKey key)
    {
        return ReadNodeProperty(node, key) is { } value ? DecodeProperty(value.Type, value.Bytes) : [];
    }

    private static (uint Type, byte[] Bytes)? ReadNodeProperty(uint node, in DevPropKey key)
    {
        var size = 0;
        if (CM_Get_DevNode_PropertyW(node, in key, out _, null, ref size, 0) != CrBufferSmall
            || size <= 0
            || size > MaximumPropertyBytes)
        {
            return null;
        }

        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(node, in key, out var type, buffer, ref size, 0) == CrSuccess
            ? (type, buffer[..size])
            : null;
    }

    private static (uint Type, byte[] Bytes)? ReadInterfaceProperty(string path, in DevPropKey key)
    {
        var size = 0;
        if (CM_Get_Device_Interface_PropertyW(path, in key, out _, null, ref size, 0) != CrBufferSmall
            || size <= 0
            || size > MaximumPropertyBytes)
        {
            return null;
        }

        var buffer = new byte[size];
        return CM_Get_Device_Interface_PropertyW(path, in key, out var type, buffer, ref size, 0) == CrSuccess
            ? (type, buffer[..size])
            : null;
    }

    /// <summary>Decodes a device property of a string, string list, GUID or FILETIME type.</summary>
    /// <param name="type">DEVPROPTYPE.</param>
    /// <param name="bytes">Property bytes.</param>
    /// <returns>The values as text; empty for other types.</returns>
    public static IReadOnlyList<string> DecodeProperty(uint type, byte[] bytes)
    {
        switch (type)
        {
            case DevPropTypeString:
            {
                var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                return text.Length == 0 ? [] : [text];
            }
            case DevPropTypeStringList:
                return Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            case DevPropTypeGuid when bytes.Length >= 16:
                return [new Guid(bytes.AsSpan(0, 16)).ToString("D")];
            case DevPropTypeFileTime when bytes.Length >= 8:
            {
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                return ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc()
                    ? []
                    : [DateTime.FromFileTimeUtc(ticks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)];
            }
            default:
                return [];
        }
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out int length, string? filter, int flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, int length, int flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint node, string deviceId, int flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint parent, uint node, int flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_ID_Size(out int length, uint node, int flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, int flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, int flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(
        uint node,
        in DevPropKey key,
        out uint type,
        [Out] byte[]? buffer,
        ref int size,
        int flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_PropertyW(
        string path,
        in DevPropKey key,
        out uint type,
        [Out] byte[]? buffer,
        ref int size,
        int flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        private readonly Guid _formatId = formatId;
        private readonly uint _propertyId = propertyId;
    }
}
