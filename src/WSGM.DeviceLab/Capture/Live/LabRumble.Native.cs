using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>One HID top-level collection, as the rumble stage sees it.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Release">Device release number.</param>
/// <param name="UsagePage">Top-level collection usage page.</param>
/// <param name="Usage">Top-level collection usage.</param>
/// <param name="OutputLength">Output report length in bytes, including the report ID.</param>
/// <param name="Path">Device interface path; kept out of the evidence.</param>
internal sealed record LabRumbleHidEndpoint(
    ushort VendorId,
    ushort ProductId,
    ushort Release,
    ushort UsagePage,
    ushort Usage,
    ushort OutputLength,
    [property: System.Text.Json.Serialization.JsonIgnore]
    string Path);

/// <summary>The Windows calls the rumble stage makes: HID enumeration and writes, and XInput.</summary>
internal static class LabRumbleNative
{
    /// <summary>XInput's "no controller in this slot" result.</summary>
    public const uint ErrorDeviceNotConnected = 1167;

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const int ErrorNoMoreItems = 259;

    /// <summary>Lists the present HID collections of one vendor, without opening them for writing.</summary>
    /// <param name="vendorId">USB vendor ID.</param>
    /// <returns>The collections whose attributes and capabilities could be read.</returns>
    public static IReadOnlyList<LabRumbleHidEndpoint> HidEndpoints(ushort vendorId)
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return [];
        }

        List<LabRumbleHidEndpoint> endpoints = [];
        try
        {
            for (uint index = 0; index < 1024; index++)
            {
                DeviceInterfaceData data = new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }

                    continue;
                }

                if (InterfacePath(set, ref data) is { } path && Describe(path) is { } endpoint
                                                           && endpoint.VendorId == vendorId)
                {
                    endpoints.Add(endpoint);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return endpoints;
    }

    /// <summary>Opens a collection for writing and checks it is still the same device.</summary>
    /// <param name="endpoint">The collection.</param>
    /// <returns>The open handle; the caller disposes it.</returns>
    public static SafeFileHandle OpenForWrite(LabRumbleHidEndpoint endpoint)
    {
        var handle = Kernel32.CreateFileW(endpoint.Path, Kernel32.GenericRead | Kernel32.GenericWrite,
            Kernel32.FileShareRead | Kernel32.FileShareWrite, 0, Kernel32.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidOperationException($"The controller could not be opened for rumble (error {error}).");
        }

        HidAttributes attributes = new() { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != endpoint.VendorId
                                                        || attributes.ProductId != endpoint.ProductId)
        {
            handle.Dispose();
            throw new InvalidOperationException("The controller changed before it could be opened for rumble.");
        }

        return handle;
    }

    /// <summary>Writes one output report.</summary>
    /// <param name="handle">Handle from <see cref="OpenForWrite" />.</param>
    /// <param name="report">Report bytes, report ID first.</param>
    /// <returns>0 on success, otherwise the Windows error code (-1 for a short write).</returns>
    public static long WriteReport(SafeFileHandle handle, byte[] report)
    {
        if (!WriteFile(handle, report, (uint)report.Length, out var written, IntPtr.Zero))
        {
            return Marshal.GetLastWin32Error();
        }

        return written == report.Length ? 0 : -1;
    }

    /// <summary>Whether an XInput slot has a controller.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    public static bool XInputConnected(uint slot)
    {
        return XInputGetState(slot, out _) == 0;
    }

    /// <summary>Reads an XInput slot's buttons.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    /// <param name="buttons">The XInput button bits, when connected.</param>
    /// <returns>Whether the slot has a controller.</returns>
    public static bool XInputButtons(uint slot, out ushort buttons)
    {
        var connected = XInputGetState(slot, out var state) == 0;
        buttons = connected ? state.Buttons : (ushort)0;
        return connected;
    }

    /// <summary>Sets both XInput motors.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    /// <param name="left">Left (low-frequency) motor, 0 to 65535.</param>
    /// <param name="right">Right (high-frequency) motor, 0 to 65535.</param>
    /// <returns>XInput's result code; 0 is success.</returns>
    public static uint XInputVibrate(uint slot, ushort left, ushort right)
    {
        XInputVibration vibration = new() { Left = left, Right = right };
        return XInputSetState(slot, ref vibration);
    }

    private static string? InterfacePath(IntPtr set, ref DeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
        if (needed is < 8 or > 16384)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal((int)needed);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on x64, and the path follows the DWORD.
            Marshal.WriteInt32(detail, 8);
            return SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    // Opens with no access, which reads attributes and capabilities without claiming the device.
    private static LabRumbleHidEndpoint? Describe(string path)
    {
        using var handle = Kernel32.CreateFileW(path, 0, Kernel32.FileShareRead | Kernel32.FileShareWrite, 0,
            Kernel32.OpenExisting, 0, 0);
        HidAttributes attributes = new() { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (handle.IsInvalid || !HidD_GetAttributes(handle, ref attributes)
                             || !HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            return HidP_GetCaps(preparsed, out var caps) < 0
                ? null
                : new LabRumbleHidEndpoint(attributes.VendorId, attributes.ProductId, attributes.Version,
                    caps.UsagePage, caps.Usage, caps.OutputReportByteLength, path);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index,
        ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data,
        IntPtr detail, uint size, out uint required, IntPtr device);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint length, out uint written,
        IntPtr overlapped);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint slot, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint slot, ref XInputVibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid Guid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLeftX;
        public short ThumbLeftY;
        public short ThumbRightX;
        public short ThumbRightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left;
        public ushort Right;
    }
}
