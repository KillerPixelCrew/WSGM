using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One HID top-level collection, read without sending any report.</summary>
internal sealed record LabHidCollection
{
    /// <summary>Device interface path.</summary>
    public required string Path { get; init; }

    /// <summary>Device instance ID.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Vendor ID, as hex.</summary>
    public string? VendorId { get; init; }

    /// <summary>Product ID, as hex.</summary>
    public string? ProductId { get; init; }

    /// <summary>Version number, as hex.</summary>
    public string? Version { get; init; }

    /// <summary>Product string.</summary>
    public string? Product { get; init; }

    /// <summary>Manufacturer string.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Top-level usage page, as hex.</summary>
    public string? UsagePage { get; init; }

    /// <summary>Top-level usage, as hex.</summary>
    public string? Usage { get; init; }

    /// <summary>Input report length in bytes, including the report ID.</summary>
    public int? InputReportLength { get; init; }

    /// <summary>Output report length in bytes, including the report ID.</summary>
    public int? OutputReportLength { get; init; }

    /// <summary>Feature report length in bytes, including the report ID.</summary>
    public int? FeatureReportLength { get; init; }

    /// <summary>Number of link collection nodes.</summary>
    public int? LinkCollections { get; init; }

    /// <summary>Input button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> InputButtons { get; init; } = [];

    /// <summary>Input value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> InputValues { get; init; } = [];

    /// <summary>Output button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> OutputButtons { get; init; } = [];

    /// <summary>Output value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> OutputValues { get; init; } = [];

    /// <summary>Feature button capabilities.</summary>
    public IReadOnlyList<LabHidCaps> FeatureButtons { get; init; } = [];

    /// <summary>Feature value capabilities.</summary>
    public IReadOnlyList<LabHidCaps> FeatureValues { get; init; } = [];

    /// <summary>What could not be read.</summary>
    public string? Problem { get; init; }
}

/// <summary>One HIDP button or value capability.</summary>
internal sealed record LabHidCaps
{
    /// <summary>Report ID.</summary>
    public required int ReportId { get; init; }

    /// <summary>Usage page, as hex.</summary>
    public required string UsagePage { get; init; }

    /// <summary>First usage, or the only one.</summary>
    public required string UsageMin { get; init; }

    /// <summary>Last usage; the same as the first when not a range.</summary>
    public required string UsageMax { get; init; }

    /// <summary>Link collection index.</summary>
    public int LinkCollection { get; init; }

    /// <summary>Whether the value is absolute rather than relative.</summary>
    public bool IsAbsolute { get; init; }

    /// <summary>Bit size of one value; 1 for buttons.</summary>
    public int BitSize { get; init; }

    /// <summary>Number of values in the report.</summary>
    public int ReportCount { get; init; }

    /// <summary>Logical minimum; values only.</summary>
    public int? LogicalMin { get; init; }

    /// <summary>Logical maximum; values only.</summary>
    public int? LogicalMax { get; init; }

    /// <summary>Physical minimum; values only.</summary>
    public int? PhysicalMin { get; init; }

    /// <summary>Physical maximum; values only.</summary>
    public int? PhysicalMax { get; init; }

    /// <summary>Unit code, as hex; values only.</summary>
    public string? Units { get; init; }

    /// <summary>Unit exponent; values only.</summary>
    public uint? UnitsExponent { get; init; }

    /// <summary>Whether the value has a null state; values only.</summary>
    public bool? HasNull { get; init; }
}

internal static partial class LabSystemDump
{
    private const int MaximumHidCollections = 512;
    private const int HidCapsBytes = 72;
    private const int HidpStatusSuccess = 0x00110000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    private static LabSystemDumpSectionResult CollectHid(LabSystemDumpContext context)
    {
        List<string> issues = [];
        HidD_GetHidGuid(out var hidGuid);
        var paths = InterfacePaths(hidGuid);
        List<LabHidCollection> collections = [];
        foreach (var path in paths.Take(MaximumHidCollections))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var collection = ReadHidCollection(path);
            if (collection.Problem is { } problem)
            {
                AddIssue(issues, $"{path}: {problem}");
            }

            collections.Add(collection);
        }

        if (paths.Count > MaximumHidCollections)
        {
            AddIssue(issues, $"Only the first {MaximumHidCollections} of {paths.Count} collections were read.");
        }

        context.Write("hid", new { collections.Count, Collections = collections });
        return Result("hid", collections.Count, Plural(collections.Count, "collection", "collections"), issues);
    }

    private static List<string> InterfacePaths(Guid interfaceClass)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in interfaceClass, null, 0) != CrSuccess
                || length <= 1)
            {
                return [];
            }

            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(in interfaceClass, null, buffer, length, 0);
            if (result == CrSuccess)
            {
                return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            }

            if (result != CrBufferSmall)
            {
                throw new InvalidOperationException($"CM_Get_Device_Interface_List failed with {result}.");
            }
        }

        throw new InvalidOperationException("The HID device list kept changing while it was read.");
    }

    private static LabHidCollection ReadHidCollection(string path)
    {
        var instanceId = ReadInterfaceProperty(path, InterfaceInstanceIdKey) is { } id
            ? DecodeProperty(id.Type, id.Bytes).FirstOrDefault()
            : null;

        // Zero desired access: the handle can query attributes and descriptors but cannot read or
        // write reports, so this never takes a device away from anything else or changes it.
        using var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return new LabHidCollection
            {
                Path = path,
                InstanceId = instanceId,
                Problem = $"could not be opened (error {Marshal.GetLastPInvokeError()})"
            };
        }

        HidAttributes attributes = new() { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        var hasAttributes = HidD_GetAttributes(handle, ref attributes);
        var product = HidString(handle, HidD_GetProductString);
        var manufacturer = HidString(handle, HidD_GetManufacturerString);
        LabHidCollection collection = new()
        {
            Path = path,
            InstanceId = instanceId,
            VendorId = hasAttributes ? Hex(attributes.VendorId, 4) : null,
            ProductId = hasAttributes ? Hex(attributes.ProductId, 4) : null,
            Version = hasAttributes ? Hex(attributes.VersionNumber, 4) : null,
            Product = product,
            Manufacturer = manufacturer
        };

        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return collection with { Problem = "no report descriptor" };
        }

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess)
            {
                return collection with { Problem = "capabilities could not be read" };
            }

            return collection with
            {
                UsagePage = Hex(caps.UsagePage, 4),
                Usage = Hex(caps.Usage, 4),
                InputReportLength = caps.InputReportByteLength,
                OutputReportLength = caps.OutputReportByteLength,
                FeatureReportLength = caps.FeatureReportByteLength,
                LinkCollections = caps.NumberLinkCollectionNodes,
                InputButtons = Caps(preparsed, 0, caps.NumberInputButtonCaps, false),
                InputValues = Caps(preparsed, 0, caps.NumberInputValueCaps, true),
                OutputButtons = Caps(preparsed, 1, caps.NumberOutputButtonCaps, false),
                OutputValues = Caps(preparsed, 1, caps.NumberOutputValueCaps, true),
                FeatureButtons = Caps(preparsed, 2, caps.NumberFeatureButtonCaps, false),
                FeatureValues = Caps(preparsed, 2, caps.NumberFeatureValueCaps, true)
            };
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? HidString(SafeFileHandle handle, Func<SafeFileHandle, byte[], uint, bool> read)
    {
        // USB string descriptors hold at most 126 UTF-16 characters.
        var buffer = new byte[256];
        if (!read(handle, buffer, (uint)buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlyList<LabHidCaps> Caps(IntPtr preparsed, int reportType, ushort count, bool values)
    {
        if (count == 0)
        {
            return [];
        }

        var length = count;
        var buffer = new byte[count * HidCapsBytes];
        var status = values
            ? HidP_GetValueCaps(reportType, buffer, ref length, preparsed)
            : HidP_GetButtonCaps(reportType, buffer, ref length, preparsed);
        if (status != HidpStatusSuccess)
        {
            return [];
        }

        List<LabHidCaps> result = [];
        for (var index = 0; index < Math.Min(length, count); index++)
        {
            result.Add(ParseHidCaps(buffer.AsSpan(index * HidCapsBytes, HidCapsBytes), values));
        }

        return result;
    }

    /// <summary>Decodes one 72-byte <c>HIDP_BUTTON_CAPS</c> or <c>HIDP_VALUE_CAPS</c> entry.</summary>
    /// <param name="entry">Raw entry.</param>
    /// <param name="value">Whether the entry is a value capability.</param>
    /// <returns>The decoded capability.</returns>
    public static LabHidCaps ParseHidCaps(ReadOnlySpan<byte> entry, bool value)
    {
        var isRange = entry[12] != 0;
        var usageMin = BinaryPrimitives.ReadUInt16LittleEndian(entry[56..]);
        var usageMax = isRange ? BinaryPrimitives.ReadUInt16LittleEndian(entry[58..]) : usageMin;
        LabHidCaps caps = new()
        {
            ReportId = entry[2],
            UsagePage = Hex(BinaryPrimitives.ReadUInt16LittleEndian(entry), 4),
            UsageMin = Hex(usageMin, 4),
            UsageMax = Hex(usageMax, 4),
            LinkCollection = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
            IsAbsolute = entry[15] != 0,
            BitSize = value ? BinaryPrimitives.ReadUInt16LittleEndian(entry[18..]) : 1,
            ReportCount = value
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry[20..])
                : BinaryPrimitives.ReadUInt16LittleEndian(entry[16..])
        };
        return !value
            ? caps
            : caps with
            {
                HasNull = entry[16] != 0,
                UnitsExponent = BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]),
                Units = Hex(BinaryPrimitives.ReadUInt32LittleEndian(entry[36..])),
                LogicalMin = BinaryPrimitives.ReadInt32LittleEndian(entry[40..]),
                LogicalMax = BinaryPrimitives.ReadInt32LittleEndian(entry[44..]),
                PhysicalMin = BinaryPrimitives.ReadInt32LittleEndian(entry[48..]),
                PhysicalMax = BinaryPrimitives.ReadInt32LittleEndian(entry[52..])
            };
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, uint length);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetManufacturerString(SafeFileHandle handle, [Out] byte[] buffer, uint length);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetButtonCaps(int reportType, [Out] byte[] caps, ref ushort length,
        IntPtr preparsed);

    [LibraryImport("hid.dll")]
    private static partial int
        HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(
        out int length,
        in Guid interfaceClass,
        string? deviceId,
        int flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_ListW(
        in Guid interfaceClass,
        string? deviceId,
        [Out] char[] buffer,
        int length,
        int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
