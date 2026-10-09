using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Windows;

namespace WSGM.DeviceLab.Transports;

/// <summary>One HID top-level collection the lab may write to.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Release">Device release number.</param>
/// <param name="UsagePage">Top-level collection usage page.</param>
/// <param name="Usage">Top-level collection usage.</param>
/// <param name="OutputLength">Output report length in bytes, including the report ID.</param>
/// <param name="Collection">The SDK's collection, which holds the device path; kept out of the evidence.</param>
internal sealed record LabHidEndpoint(
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
    public static LabHidEndpoint From(HidCollection collection)
    {
        return new LabHidEndpoint(collection.VendorId, collection.ProductId, collection.ReleaseNumber,
            collection.UsagePage, collection.Usage, collection.OutputLength, collection);
    }
}

/// <summary>Plain HID enumeration and output writes, shared by rumble, lighting and controller commands.</summary>
internal static class LabHid
{
    /// <summary>Lists the present HID collections of one vendor, without opening them for writing.</summary>
    /// <param name="vendorId">USB vendor ID.</param>
    /// <returns>The collections whose attributes and capabilities could be read.</returns>
    /// <exception cref="Win32Exception">Windows could not list the HID collections.</exception>
    public static IReadOnlyList<LabHidEndpoint> HidEndpoints(ushort vendorId)
    {
        return
        [
            .. HidDevices.EnumerateAll().Where(collection => collection.VendorId == vendorId)
                .Select(LabHidEndpoint.From)
        ];
    }

    /// <summary>Opens a collection for writing.</summary>
    /// <param name="endpoint">The collection.</param>
    /// <returns>The open, synchronous handle; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">The collection could not be opened.</exception>
    public static SafeFileHandle OpenForWrite(LabHidEndpoint endpoint)
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint length, out uint written,
        IntPtr overlapped);
}
