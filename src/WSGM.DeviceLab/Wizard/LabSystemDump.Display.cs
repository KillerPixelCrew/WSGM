using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.DeviceLab.Wizard;

internal static partial class LabSystemDump
{
    private const int EnumCurrentSettings = -1;
    private const uint EddGetDeviceInterfaceName = 1;
    private const uint DisplayDeviceAttachedToDesktop = 0x1;
    private const uint DisplayDevicePrimary = 0x4;
    private const uint QdcOnlyActivePaths = 2;
    private const int PathInfoBytes = 72;
    private const int ModeInfoBytes = 64;
    private const int MaximumDisplayModes = 256;

    private static LabSystemDumpSectionResult CollectDisplay(LabSystemDumpContext context)
    {
        List<string> issues = [];
        List<object> adapters = [];
        var active = 0;
        for (uint index = 0; index < 32; index++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var adapter = NewDisplayDevice();
            if (!EnumDisplayDevicesW(null, index, ref adapter, 0))
            {
                break;
            }

            List<object> monitors = [];
            for (uint monitorIndex = 0; monitorIndex < 16; monitorIndex++)
            {
                var monitor = NewDisplayDevice();
                if (!EnumDisplayDevicesW(adapter.DeviceName, monitorIndex, ref monitor, EddGetDeviceInterfaceName))
                {
                    break;
                }

                monitors.Add(new { monitor.DeviceString, Path = monitor.DeviceId, State = Hex(monitor.StateFlags) });
            }

            var attached = (adapter.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            active += attached ? 1 : 0;
            adapters.Add(new
            {
                adapter.DeviceName,
                adapter.DeviceString,
                adapter.DeviceId,
                State = Hex(adapter.StateFlags),
                Attached = attached,
                Primary = (adapter.StateFlags & DisplayDevicePrimary) != 0,
                Current = attached ? CurrentMode(adapter.DeviceName) : null,
                Modes = attached ? Modes(adapter.DeviceName) : null,
                Monitors = monitors
            });
        }

        object paths;
        try
        {
            paths = DisplayPaths();
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException)
        {
            AddIssue(issues, $"Display paths: {ex.Message}");
            paths = new { Problem = ex.Message };
        }

        var brightness = WmiSection(@"root\wmi", "WmiMonitorBrightness",
            ["InstanceName", "Active", "CurrentBrightness", "Levels", "Level"], issues, context.Cancellation);
        context.Write("display", new { Adapters = adapters, Paths = paths, Brightness = brightness, Issues = issues });
        return Result("display", active, Plural(active, "screen", "screens"), issues);
    }

    private static DisplayDevice NewDisplayDevice()
    {
        return new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
    }

    private static object? CurrentMode(string device)
    {
        var mode = NewDevMode();
        return EnumDisplaySettingsExW(device, EnumCurrentSettings, ref mode, 0) ? Mode(mode) : null;
    }

    private static List<object> Modes(string device)
    {
        HashSet<(uint, uint, uint, uint)> seen = [];
        List<object> modes = [];
        for (var index = 0; index < 4096 && modes.Count < MaximumDisplayModes; index++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettingsExW(device, index, ref mode, 0))
            {
                break;
            }

            if (seen.Add((mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency, mode.BitsPerPel)))
            {
                modes.Add(new { Width = mode.PelsWidth, Height = mode.PelsHeight, Hz = mode.DisplayFrequency, Bits = mode.BitsPerPel });
            }
        }

        return modes;
    }

    private static object Mode(DevMode mode)
    {
        return new
        {
            Width = mode.PelsWidth,
            Height = mode.PelsHeight,
            Hz = mode.DisplayFrequency,
            Bits = mode.BitsPerPel,
            Orientation = mode.DisplayOrientation switch
            {
                0 => "landscape",
                1 => "portrait (90)",
                2 => "landscape flipped (180)",
                3 => "portrait flipped (270)",
                _ => mode.DisplayOrientation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            X = mode.PositionX,
            Y = mode.PositionY
        };
    }

    private static DevMode NewDevMode()
    {
        return new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
    }

    private static List<object> DisplayPaths()
    {
        byte[] paths;
        byte[] modes;
        uint pathCount;
        uint modeCount;
        var attempt = 0;
        while (true)
        {
            var sizes = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);
            if (sizes != 0)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with {sizes}.");
            }

            paths = new byte[pathCount * PathInfoBytes];
            modes = new byte[modeCount * ModeInfoBytes];
            var result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 0)
            {
                break;
            }

            // ERROR_INSUFFICIENT_BUFFER: the topology changed between the two calls.
            if (result != 122 || ++attempt >= 4)
            {
                throw new InvalidOperationException($"QueryDisplayConfig failed with {result}.");
            }
        }

        List<object> entries = [];
        for (var index = 0; index < pathCount; index++)
        {
            var path = paths.AsSpan(index * PathInfoBytes, PathInfoBytes);
            var sourceAdapter = path[..8].ToArray();
            var sourceId = BinaryPrimitives.ReadUInt32LittleEndian(path[8..]);
            var targetAdapter = path.Slice(20, 8).ToArray();
            var targetId = BinaryPrimitives.ReadUInt32LittleEndian(path[28..]);
            var technology = BinaryPrimitives.ReadUInt32LittleEndian(path[36..]);
            var rotation = BinaryPrimitives.ReadUInt32LittleEndian(path[40..]);
            var refreshNumerator = BinaryPrimitives.ReadUInt32LittleEndian(path[48..]);
            var refreshDenominator = BinaryPrimitives.ReadUInt32LittleEndian(path[52..]);
            entries.Add(new
            {
                Source = SourceName(sourceAdapter, sourceId),
                Target = TargetName(targetAdapter, targetId),
                PreferredMode = PreferredMode(targetAdapter, targetId),
                Connection = technology switch
                {
                    0x80000000 => "internal",
                    0 => "VGA",
                    4 => "DVI",
                    5 => "HDMI",
                    10 => "DisplayPort",
                    11 => "embedded DisplayPort",
                    12 => "embedded DisplayPort",
                    13 => "UDI embedded",
                    15 => "Miracast",
                    _ => Hex(technology)
                },
                Rotation = rotation switch
                {
                    1 => "none",
                    2 => "90",
                    3 => "180",
                    4 => "270",
                    _ => rotation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                RefreshHz = refreshDenominator == 0 ? (double?)null : Math.Round((double)refreshNumerator / refreshDenominator, 3)
            });
        }

        return entries;
    }

    private static string? SourceName(byte[] adapter, uint id)
    {
        var packet = DeviceInfoPacket(1, 84, adapter, id);
        return DisplayConfigGetDeviceInfo(packet) == 0 ? Utf16Field(packet.AsSpan(20, 64)) : null;
    }

    private static object? TargetName(byte[] adapter, uint id)
    {
        var packet = DeviceInfoPacket(2, 420, adapter, id);
        if (DisplayConfigGetDeviceInfo(packet) != 0)
        {
            return null;
        }

        var manufacturer = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(28));
        var product = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(30));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20));
        var edidValid = (flags & 0x2) != 0;
        return new
        {
            Name = Utf16Field(packet.AsSpan(36, 128)),
            EdidManufacturer = edidValid ? EdidManufacturer(manufacturer) : null,
            EdidProductCode = edidValid ? Hex(product, 4) : null,
            Path = Utf16Field(packet.AsSpan(164, 256)),
            Edid = EdidFromRegistry(Utf16Field(packet.AsSpan(164, 256)))
        };
    }

    private static object? PreferredMode(byte[] adapter, uint id)
    {
        var packet = DeviceInfoPacket(3, 80, adapter, id);
        if (DisplayConfigGetDeviceInfo(packet) != 0)
        {
            return null;
        }

        var numerator = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(48));
        var denominator = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(52));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24));
        return new
        {
            Width = width,
            Height = height,

            // The panel's native mode: a handheld panel that is natively portrait reports a taller
            // preferred mode even when Windows shows it rotated to landscape.
            NativeOrientation = width < height ? "portrait" : "landscape",
            RefreshHz = denominator == 0 ? (double?)null : Math.Round((double)numerator / denominator, 3)
        };
    }

    /// <summary>Decodes the three-letter PNP manufacturer ID Windows reports for a monitor's EDID.</summary>
    /// <param name="value">The value as Windows reports it; the EDID bytes in little-endian order.</param>
    /// <returns>Three upper-case letters, for example DEL.</returns>
    public static string EdidManufacturer(ushort value)
    {
        var id = BinaryPrimitives.ReverseEndianness(value);
        Span<char> letters =
        [
            (char)('@' + ((id >> 10) & 0x1F)),
            (char)('@' + ((id >> 5) & 0x1F)),
            (char)('@' + (id & 0x1F))
        ];
        return new string(letters);
    }

    private static byte[] DeviceInfoPacket(uint type, int size, byte[] adapter, uint id)
    {
        var packet = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)size);
        adapter.CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), id);
        return packet;
    }

    private static string? Utf16Field(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        text = end >= 0 ? text[..end] : text;
        return text.Length == 0 ? null : text;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string device, int mode, ref DevMode devMode, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] byte[] paths,
        ref uint modeCount,
        [Out] byte[] modes,
        IntPtr topology);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo([In] [Out] byte[] packet);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;

        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
