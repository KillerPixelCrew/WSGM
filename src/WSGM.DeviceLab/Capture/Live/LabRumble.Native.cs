using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using WSGM.Device.Sdk.Windows;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>One HID top-level collection, as the rumble stage sees it.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Release">Device release number.</param>
/// <param name="UsagePage">Top-level collection usage page.</param>
/// <param name="Usage">Top-level collection usage.</param>
/// <param name="OutputLength">Output report length in bytes, including the report ID.</param>
/// <param name="Collection">The SDK's collection, which holds the device path; kept out of the evidence.</param>
internal sealed record LabRumbleHidEndpoint(
    ushort VendorId,
    ushort ProductId,
    ushort Release,
    ushort UsagePage,
    ushort Usage,
    ushort OutputLength,
    [property: JsonIgnore] HidCollection Collection)
{
    /// <summary>The endpoint for one enumerated collection.</summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The endpoint.</returns>
    public static LabRumbleHidEndpoint From(HidCollection collection)
    {
        return new LabRumbleHidEndpoint(collection.VendorId, collection.ProductId, collection.ReleaseNumber,
            collection.UsagePage, collection.Usage, collection.OutputLength, collection);
    }
}

/// <summary>The Windows calls the rumble stage makes: HID enumeration and writes, and XInput.</summary>
internal static class LabRumbleNative
{
    /// <summary>XInput's "no controller in this slot" result.</summary>
    public const uint ErrorDeviceNotConnected = 1167;

    /// <summary>Lists the present HID collections of one vendor, without opening them for writing.</summary>
    /// <param name="vendorId">USB vendor ID.</param>
    /// <returns>The collections whose attributes and capabilities could be read.</returns>
    /// <exception cref="Win32Exception">Windows could not list the HID collections.</exception>
    public static IReadOnlyList<LabRumbleHidEndpoint> HidEndpoints(ushort vendorId)
    {
        return
        [
            .. HidDevices.EnumerateAll().Where(collection => collection.VendorId == vendorId)
                .Select(LabRumbleHidEndpoint.From)
        ];
    }

    /// <summary>Opens a collection for writing.</summary>
    /// <param name="endpoint">The collection.</param>
    /// <returns>The open, synchronous handle; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">The collection could not be opened.</exception>
    public static SafeFileHandle OpenForWrite(LabRumbleHidEndpoint endpoint)
    {
        try
        {
            return HidDevices.Open(endpoint.Collection, false);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"The controller could not be opened for writing (error {ex.NativeErrorCode}).", ex);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint length, out uint written,
        IntPtr overlapped);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint slot, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint slot, ref XInputVibration vibration);

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
