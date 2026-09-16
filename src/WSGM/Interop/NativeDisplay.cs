using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>
/// The display P/Invoke surface: DisplayConfig packets (per-monitor DPI scaling, advanced color,
/// GDI source names) and display device enumeration. Mode enumeration and changes go through
/// WindowsDeviceControl.DisplayModes, which serializes them with the overlay's mode changes. The DPI packets
/// (types -3/-4) are undocumented but ABI-stable — the same mechanism the Settings app uses. Every
/// packet is blittable; keep layouts exactly as verified.
/// </summary>
internal static unsafe partial class NativeDisplay
{
    internal const int GetDpiScaleType = -3;
    internal const int SetDpiScaleType = -4;
    internal const int GetSourceNameType = 1;   // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    internal const uint QdcOnlyActivePaths = 0x00000002;

    internal const uint DisplayDeviceActive = 0x00000001;
    internal const uint DisplayDevicePrimary = 0x00000004;
    internal const uint GetDeviceInterfaceName = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;             // SOURCE id for DPI/name packets, TARGET id for advanced color
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleGet     // 0x20 bytes; field order min,cur,max (verified)
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleSet     // 0x18 bytes
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx;
        public uint OutputTechnology; public uint Rotation; public uint Scaling;
        public uint RefreshRateNumerator; public uint RefreshRateDenominator;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceDeviceName   // DISPLAYCONFIG_SOURCE_DEVICE_NAME, 0x54 bytes
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];   // UTF-16 GDI name, e.g. \\.\DISPLAY1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public uint Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] PathInfo[] paths,
        ref uint numModes, [In, Out] ModeInfo[] modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(ref DpiScaleGet packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigSetDeviceInfo(ref DpiScaleSet packet);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayDevices(char* device, uint index, ref DisplayDevice displayDevice, uint flags);

}
