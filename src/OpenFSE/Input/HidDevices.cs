using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenFSE.Input;

/// <summary>Thin SetupAPI/hid.dll plumbing shared by the controller readers.</summary>
internal static partial class HidDevices
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidpCaps
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    /// <summary>All present HID device paths with their vendor/product ids.</summary>
    public static IEnumerable<(string Path, ushort Vid, ushort Pid)> Enumerate()
    {
        foreach (var path in EnumeratePaths())
        {
            var handle = OpenRead(path);
            if (handle == -1)
            {
                continue;
            }
            var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
            var ok = HidD_GetAttributes(handle, ref attributes);
            Close(handle);
            if (ok)
            {
                yield return (path, attributes.VendorID, attributes.ProductID);
            }
        }
    }

    public static nint OpenRead(string path) =>
        CreateFileW(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);

    public static bool TryGetPreparsed(nint handle, out nint preparsed, out HidpCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out preparsed))
        {
            return false;
        }
        if (HidP_GetCaps(preparsed, out caps) != HidpStatusSuccess)
        {
            HidD_FreePreparsedData(preparsed);
            preparsed = 0;
            return false;
        }
        return true;
    }

    public static void FreePreparsed(nint preparsed)
    {
        if (preparsed != 0)
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    public static bool Read(nint handle, byte[] buffer, out uint read) =>
        ReadFile(handle, buffer, (uint)buffer.Length, out read, 0);

    public static void Close(nint handle)
    {
        if (handle != -1 && handle != 0)
        {
            CancelIoEx(handle, 0);
            CloseHandle(handle);
        }
    }

    private static IEnumerable<string> EnumeratePaths()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1)
        {
            yield break;
        }
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref data); index++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var required, 0);
                if (required == 0)
                {
                    continue;
                }
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, 0))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (!string.IsNullOrEmpty(path))
                        {
                            yield return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(nint device, ref HiddAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(nint device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(nint preparsedData);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, nint detailData, uint detailSize,
        out uint requiredSize, nint deviceInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(nint file, [Out] byte[] buffer, uint numberOfBytesToRead,
        out uint numberOfBytesRead, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(nint file, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
