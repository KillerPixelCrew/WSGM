using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenFSE.Core;

namespace OpenFSE.Input;

/// <summary>Reads a Steam Deck controller's HID input reports so OpenFSE can bind the
/// buttons XInput cannot see: the L4/R4/L5/R5 paddles, Steam and Quick Access.
/// Handheld Companion's Steam Deck emulation presents exactly this device, so the
/// same code covers a real Deck and HC's virtual pad on any handheld.
///
/// Report layout taken from Valve's Deck reports (as implemented in
/// steam-hidapi.net): a 64-byte report starting 0x01 0x00, event type at +2
/// (0x09 = input state), then button bitfields at +8..+14.</summary>
public sealed partial class SteamDeckHid : IDisposable
{
    private const ushort ValveVid = 0x28DE;
    private const ushort DeckPid = 0x1205;
    private const byte DeckInputData = 0x09;
    private const int ReportLength = 64;

    private Thread? _thread;
    private volatile bool _running;
    private nint _handle = -1;

    /// <summary>Latest button state, polled by GamepadService. Zero when no device.</summary>
    public GamepadButtons State { get; private set; }

    public bool IsConnected => _handle != -1;

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "OpenFSE.SteamDeckHid" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_handle != -1)
        {
            CancelIoEx(_handle, 0);
            CloseHandle(_handle);
            _handle = -1;
        }
        State = 0;
    }

    private void ReadLoop()
    {
        while (_running)
        {
            if (_handle == -1)
            {
                _handle = OpenDeckDevice();
                if (_handle == -1)
                {
                    Thread.Sleep(3000);     // no Deck device — retry later
                    continue;
                }
                Log.Info("Steam Deck controller HID opened (extra buttons available).");
            }

            var buffer = new byte[ReportLength + 1];
            if (!ReadFile(_handle, buffer, (uint)buffer.Length, out var read, 0) || read == 0)
            {
                if (_running)
                {
                    Log.Warn("Steam Deck HID read failed — reopening.");
                }
                CloseHandle(_handle);
                _handle = -1;
                State = 0;
                continue;
            }

            // Windows may prepend a report-ID byte; find the 0x01 0x00 header.
            var offset = -1;
            for (var i = 0; i <= 1 && i + 14 < read; i++)
            {
                if (buffer[i] == 0x01 && buffer[i + 1] == 0x00)
                {
                    offset = i;
                    break;
                }
            }
            if (offset < 0 || buffer[offset + 2] != DeckInputData)
            {
                continue;   // not an input-state report
            }

            State = Parse(buffer, offset);
        }
    }

    private static GamepadButtons Parse(byte[] b, int o)
    {
        byte b0 = b[o + 8], b1 = b[o + 9], b2 = b[o + 10], b3 = b[o + 11], b5 = b[o + 13], b6 = b[o + 14];
        GamepadButtons state = 0;

        if ((b0 & 0x80) != 0) state |= GamepadButtons.A;
        if ((b0 & 0x20) != 0) state |= GamepadButtons.B;
        if ((b0 & 0x40) != 0) state |= GamepadButtons.X;
        if ((b0 & 0x10) != 0) state |= GamepadButtons.Y;
        if ((b0 & 0x08) != 0) state |= GamepadButtons.LeftShoulder;
        if ((b0 & 0x04) != 0) state |= GamepadButtons.RightShoulder;
        if ((b0 & 0x02) != 0) state |= GamepadButtons.LeftTrigger;
        if ((b0 & 0x01) != 0) state |= GamepadButtons.RightTrigger;

        if ((b1 & 0x01) != 0) state |= GamepadButtons.DPadUp;
        if ((b1 & 0x02) != 0) state |= GamepadButtons.DPadRight;
        if ((b1 & 0x04) != 0) state |= GamepadButtons.DPadLeft;
        if ((b1 & 0x08) != 0) state |= GamepadButtons.DPadDown;
        if ((b1 & 0x10) != 0) state |= GamepadButtons.Start;         // Menu (☰)
        if ((b1 & 0x20) != 0) state |= GamepadButtons.Steam;
        if ((b1 & 0x40) != 0) state |= GamepadButtons.Back;          // Options (⧉)
        if ((b1 & 0x80) != 0) state |= GamepadButtons.L5;

        if ((b2 & 0x01) != 0) state |= GamepadButtons.R5;
        if ((b2 & 0x02) != 0) state |= GamepadButtons.LeftPadPress;
        if ((b2 & 0x04) != 0) state |= GamepadButtons.RightPadPress;
        if ((b2 & 0x40) != 0) state |= GamepadButtons.LeftThumb;

        if ((b3 & 0x04) != 0) state |= GamepadButtons.RightThumb;

        if ((b5 & 0x02) != 0) state |= GamepadButtons.L4;
        if ((b5 & 0x04) != 0) state |= GamepadButtons.R4;

        if ((b6 & 0x04) != 0) state |= GamepadButtons.QuickAccess;

        return state;
    }

    // ---- device enumeration (SetupAPI + hid.dll) ----

    private static nint OpenDeckDevice()
    {
        foreach (var path in EnumerateHidPaths())
        {
            var handle = CreateFileW(path, GenericRead, FileShareRead | FileShareWrite,
                0, OpenExisting, 0, 0);
            if (handle == -1)
            {
                continue;
            }

            var attributes = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
            if (HidD_GetAttributes(handle, ref attributes) &&
                attributes.VendorID == ValveVid && attributes.ProductID == DeckPid &&
                HasLargeInputReport(handle))
            {
                return handle;
            }
            CloseHandle(handle);
        }
        return -1;
    }

    private static bool HasLargeInputReport(nint handle)
    {
        // The Deck exposes several HID collections; the controller-state one uses
        // 64-byte input reports (+1 for the report id).
        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return false;
        }
        try
        {
            return HidP_GetCaps(preparsed, out var caps) == 0x00110000 && caps.InputReportByteLength >= ReportLength;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static IEnumerable<string> EnumerateHidPaths()
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
                    // cbSize is the size of the fixed part: 4-byte DWORD + char (padded to 8 on x64... 6 in practice)
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

    public void Dispose() => Stop();

    // ---- interop ----

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
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
