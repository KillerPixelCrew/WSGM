using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.AllyXLab;

internal sealed record HidEndpoint(string Path, ushort Vid, ushort Pid, ushort Release, ushort Page, ushort Usage,
    ushort InputBytes, ushort OutputBytes, ushort FeatureBytes)
{
    internal string Id => SessionLog.Token(Path.ToUpperInvariant());
    internal object Public => new { Id, Vid, Pid, Release, Page, Usage, InputBytes, OutputBytes, FeatureBytes };
    internal bool Ally => Vid == 0x0B05 && Pid == 0x1B4C;
    internal bool Vendor => Ally && Page == 0xFF31 && Usage == 0x80;
    internal bool Rumble => Ally && ((Page == 1 && Usage == 5) || (Page == 15 && Usage is 2 or 0x21));
}

internal static class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceData
    { internal uint Size; internal Guid Guid; internal uint Flags; internal UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    { internal uint Size; internal ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        internal ushort Usage, Page, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort Nodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint length, out uint written, IntPtr overlapped);

    internal static List<HidEndpoint> Enumerate()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception();
        }

        List<HidEndpoint> endpoints = [];
        try
        {
            for (uint i = 0; i < 512; i++)
            {
                InterfaceData data = new() { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    if (Marshal.GetLastWin32Error() != 259)
                    {
                        throw new Win32Exception();
                    }

                    break;
                }
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed is < 8 or > 16384)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    Attributes attributes = new() { Size = (uint)Marshal.SizeOf<Attributes>() };
                    if (handle.IsInvalid || !HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out IntPtr preparsed))
                    {
                        continue;
                    }

                    try
                    {
                        if (HidP_GetCaps(preparsed, out Caps caps) < 0)
                        {
                            continue;
                        }
                        // Sensor collections are metadata-only here; raw capture stays on ASUS interfaces.
                        if ((attributes.Vid == 0x0B05 && attributes.Pid == 0x1B4C) || caps.Page == 0x20)
                        {
                            endpoints.Add(new(path, attributes.Vid, attributes.Pid, attributes.Version,
                                caps.Page, caps.Usage, caps.Input, caps.Output, caps.Feature));
                        }
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return endpoints;
    }

    internal static SafeFileHandle Open(HidEndpoint endpoint)
    {
        var handle = CreateFile(endpoint.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        Attributes attributes = new() { Size = (uint)Marshal.SizeOf<Attributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.Vid != endpoint.Vid || attributes.Pid != endpoint.Pid || attributes.Version != endpoint.Release)
        { handle.Dispose(); throw new InvalidOperationException("HID identity changed before acquisition."); }
        return handle;
    }

    internal static void Output(SafeFileHandle handle, HidEndpoint endpoint, byte[] bytes, SessionLog log)
    {
        if (endpoint.OutputBytes < bytes.Length || endpoint.OutputBytes > 1024)
        {
            throw new InvalidOperationException("Output report length does not match the reference.");
        }

        byte[] padded = new byte[endpoint.OutputBytes];
        bytes.CopyTo(padded, 0);
        log.Add("hid-output-attempt", new { Endpoint = endpoint.Id, Bytes = Convert.ToHexString(padded) });
        if (!WriteFile(handle, padded, (uint)padded.Length, out uint written, IntPtr.Zero) || written != padded.Length)
        {
            throw new IOException("HID output failed or was short; effect is unknown. No retry.");
        }

        log.Add("hid-output-returned", new { Endpoint = endpoint.Id, Written = written, Verified = false });
    }
}
