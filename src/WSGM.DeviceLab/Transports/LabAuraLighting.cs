using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>The HID endpoint an Aura test writes to, without its device path.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Release">USB release number.</param>
/// <param name="UsagePage">Top-level usage page.</param>
/// <param name="Usage">Top-level usage.</param>
/// <param name="OutputBytes">Output report length.</param>
internal sealed record LabAuraEndpoint(
    string VendorId,
    string ProductId,
    string Release,
    string UsagePage,
    string Usage,
    int OutputBytes);

/// <summary>
///     The Aura checkpoint marker. The lights are write-only, so there is no colour to capture; the
///     checkpoint only records that the lab is about to write them, and the tester sets their colour again.
/// </summary>
/// <param name="Endpoint">The endpoint about to be written.</param>
/// <param name="Note">What restoring takes.</param>
internal sealed record LabAuraOriginal(LabAuraEndpoint Endpoint, string Note);

/// <summary>
///     The Aura service the wizard calls through the hardware worker (<c>aura</c>, opened with a
///     <see cref="LabAuraLayout" />). Writes are refused until the worker's checkpoint is acknowledged.
/// </summary>
internal interface ILabAura : IDisposable
{
    /// <summary>The checkpoint marker; nothing is read from the lights.</summary>
    /// <returns>The endpoint and the restore note.</returns>
    [LabWorkerSnapshot]
    LabAuraOriginal Original();

    /// <summary>Shows one colour on one zone.</summary>
    /// <param name="zone">0 both rings, 1 left outer, 2 left inner, 3 right inner, 4 right outer.</param>
    /// <param name="channel">0 red, 1 green, 2 blue.</param>
    [LabWorkerWrite]
    void Colour(int zone, int channel);

    /// <summary>Turns the brightness to off.</summary>
    [LabWorkerWrite]
    void Off();
}

/// <summary>
///     ROG Ally Aura lighting over the vendor HID interface, ported from AllyXLab's Worker. The lights
///     are write-only: the tester's colour cannot be read, so it cannot be put back. It runs only inside
///     the hardware worker, behind <see cref="ILabAura" />.
/// </summary>
internal sealed class LabAuraLighting : ILabAura
{
    /// <summary>The number of lighting zones this test steps through (both rings, then the four half-rings).</summary>
    public const int ZoneCount = 5;

    private readonly SafeFileHandle _handle;
    private readonly LabPowerLog _log;

    private LabAuraLighting(SafeFileHandle handle, LabAuraEndpoint endpoint, LabPowerLog log)
    {
        _handle = handle;
        Endpoint = endpoint;
        _log = log;
    }

    /// <summary>The worker service registration. Opening fails when there is not exactly one endpoint.</summary>
    public static LabWorkerService Service { get; } = new("aura", typeof(ILabAura),
        (args, log) => Open(LabWorkerService.Arg<LabAuraLayout>(args, 0), log)
                       ?? throw new InvalidOperationException("The device's own lighting interface was not found."));

    /// <summary>The endpoint in use.</summary>
    public LabAuraEndpoint Endpoint { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle.Dispose();
    }

    /// <inheritdoc />
    public LabAuraOriginal Original()
    {
        return new LabAuraOriginal(Endpoint,
            "Write-only lighting: the colour cannot be read, so the tester sets it again in Armoury Crate.");
    }

    /// <summary>Shows one colour on one zone: AllyXLab's init, brightness, static colour and apply.</summary>
    /// <param name="zone">0 both rings, 1 left outer, 2 left inner, 3 right inner, 4 right outer.</param>
    /// <param name="channel">0 red, 1 green, 2 blue.</param>
    public void Colour(int zone, int channel)
    {
        if (zone is < 0 or >= ZoneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(zone));
        }

        if (channel is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Output([0x5A, .. Encoding.ASCII.GetBytes("ASUS Tech.Inc.")]);
        Output([0x5A, 0xBA, 0xC5, 0xC4, 1]);
        var colour = new byte[64];
        colour[0] = 0x5A;
        colour[1] = 0xB3;
        colour[2] = (byte)zone;
        colour[4 + channel] = 80;
        Output(colour);
        Output([0x5A, 0xB5]);
        Output([0x5A, 0xB4]);
    }

    /// <summary>Turns the brightness to off, as AllyXLab does after every flash.</summary>
    public void Off()
    {
        Output([0x5A, 0xBA, 0xC5, 0xC4, 0]);
    }

    /// <summary>Opens the exact endpoint the record names; refuses when there is not exactly one.</summary>
    /// <param name="layout">The endpoint identity.</param>
    /// <param name="log">Where every write is logged.</param>
    /// <returns>The lighting, or null with the reason logged.</returns>
    public static LabAuraLighting? Open(LabAuraLayout layout, LabPowerLog log)
    {
        List<(string Path, Attributes Attributes, Caps Caps)> matches = [];
        foreach (var (path, attributes, caps) in Enumerate())
        {
            if (attributes.Vid == layout.VendorId && attributes.Pid == layout.ProductId
                                                  && caps.Page == layout.UsagePage && caps.Usage == layout.Usage)
            {
                matches.Add((path, attributes, caps));
            }
        }

        if (matches.Count != 1)
        {
            log.Add("aura-endpoint", new { Found = matches.Count, Reason = "exactly one endpoint is required" });
            return null;
        }

        var (endpointPath, identity, capabilities) = matches[0];
        LabAuraEndpoint endpoint = new($"{identity.Vid:X4}", $"{identity.Pid:X4}", $"{identity.Version:X4}",
            $"{capabilities.Page:X4}", $"{capabilities.Usage:X4}", capabilities.Output);
        if (capabilities.Output is < 64 or > 1024)
        {
            log.Add("aura-endpoint", new { Endpoint = endpoint, Reason = "the output report is not 64 bytes or more" });
            return null;
        }

        var handle = CreateFile(endpointPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            log.Add("aura-endpoint", new { Endpoint = endpoint, Reason = new Win32Exception(error).Message });
            return null;
        }

        var check = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
        if (!HidD_GetAttributes(handle, ref check) || check.Vid != identity.Vid || check.Pid != identity.Pid
            || check.Version != identity.Version)
        {
            handle.Dispose();
            log.Add("aura-endpoint", new { Endpoint = endpoint, Reason = "the device changed while it was opened" });
            return null;
        }

        log.Add("aura-endpoint", new { Endpoint = endpoint, Opened = true });
        return new LabAuraLighting(handle, endpoint, log);
    }

    private void Output(byte[] bytes)
    {
        var padded = new byte[Endpoint.OutputBytes];
        bytes.CopyTo(padded, 0);
        _log.Add("hid-output", new { Bytes = Convert.ToHexString(bytes) });
        if (!WriteFile(_handle, padded, (uint)padded.Length, out var written, IntPtr.Zero) || written != padded.Length)
        {
            _log.Add("hid-output-failed", new { Error = Marshal.GetLastWin32Error(), Written = written });
            throw new IOException(
                "The lighting write failed or was short; its effect is unknown and it was not tried again.");
        }

        _log.Add("hid-output-returned", new { Written = written, Verified = false });
    }

    private static List<(string Path, Attributes Attributes, Caps Caps)> Enumerate()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception();
        }

        List<(string, Attributes, Caps)> endpoints = [];
        try
        {
            for (uint i = 0; i < 512; i++)
            {
                InterfaceData data = new() { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    break;
                }

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed is < 8 or > 16384)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
                    if (handle.IsInvalid || !HidD_GetAttributes(handle, ref attributes)
                                         || !HidD_GetPreparsedData(handle, out var preparsed))
                    {
                        continue;
                    }

                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) >= 0)
                        {
                            endpoints.Add((path, attributes, caps));
                        }
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsed);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return endpoints;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out Caps caps);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint length, out uint written,
        IntPtr overlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index,
        ref InterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail,
        uint size, out uint required, IntPtr device);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        public uint Size;
        public Guid Guid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        public uint Size;
        public ushort Vid;
        public ushort Pid;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Caps
    {
        public ushort Usage;
        public ushort Page;
        public ushort Input;
        public ushort Output;
        public ushort Feature;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort Nodes;
        public ushort InputButtons;
        public ushort InputValues;
        public ushort InputIndices;
        public ushort OutputButtons;
        public ushort OutputValues;
        public ushort OutputIndices;
        public ushort FeatureButtons;
        public ushort FeatureValues;
        public ushort FeatureIndices;
    }
}
