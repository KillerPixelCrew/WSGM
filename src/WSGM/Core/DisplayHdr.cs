using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>Queries and switches Windows HDR (DisplayConfig advanced color) per active display.</summary>
internal static unsafe partial class DisplayHdr
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int GetSourceNameType = 1;
    private const int GetAdvancedColorInfoType = 9;
    private const int SetAdvancedColorStateType = 10;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public int Type; public uint Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx;
        public uint OutputTechnology; public uint Rotation; public uint Scaling;
        public uint RefreshRateNumerator; public uint RefreshRateDenominator;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo SourceInfo; public PathTargetInfo TargetInfo; public uint Flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorState { public DeviceInfoHeader Header; public uint EnableAdvancedColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    private readonly record struct Target(string DeviceName, Luid AdapterId, uint TargetId, bool Supported, bool Enabled);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] PathInfo[] paths,
        ref uint numModes, [In, Out] ModeInfo[] modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigSetDeviceInfo(ref AdvancedColorState packet);

    /// <summary>Gets HDR availability and current state keyed by GDI source name.</summary>
    internal static Dictionary<string, (bool Available, bool Enabled)> ReadActive()
    {
        var result = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in EnumerateTargets())
        {
            result.TryAdd(target.DeviceName, (target.Supported, target.Enabled));
        }
        return result;
    }

    /// <summary>Applies the selected profile's HDR flag only to monitors that currently support HDR.</summary>
    internal static void Apply(IEnumerable<MonitorDisplayProfile> profiles, bool game)
    {
        foreach (var target in EnumerateTargets())
        {
            MonitorDisplayProfile? profile = null;
            foreach (var candidate in profiles)
            {
                if (string.Equals(candidate.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = candidate;
                    break;
                }
            }
            if (profile is null)
            {
                continue;
            }
            var enabled = game ? profile.Game.HdrEnabled : profile.Desktop.HdrEnabled;
            if (!ShouldChange(target.Supported, target.Enabled, enabled))
            {
                continue;
            }
            var packet = new AdvancedColorState
            {
                Header =
                {
                    Type = SetAdvancedColorStateType,
                    Size = (uint)sizeof(AdvancedColorState),
                    AdapterId = target.AdapterId,
                    Id = target.TargetId,
                },
                EnableAdvancedColor = enabled ? 1u : 0u,
            };
            var status = DisplayConfigSetDeviceInfo(ref packet);
            if (status == 0)
            {
                Log.Info($"Display profile: {target.DeviceName} HDR -> {(enabled ? "on" : "off")}.");
            }
            else
            {
                Log.Warn($"Display profile: {target.DeviceName} HDR {(enabled ? "enable" : "disable")} failed ({status}).");
            }
        }
    }

    internal static bool ShouldChange(bool available, bool current, bool requested)
        => available && current != requested;

    private static List<Target> EnumerateTargets()
    {
        var result = new List<Target>();
        int status;
        uint numPaths;
        PathInfo[] paths;
        var attempts = 0;
        do
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out numPaths, out var numModes) != 0)
            {
                return result;
            }
            paths = new PathInfo[numPaths];
            var modes = new ModeInfo[numModes];
            status = QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, 0);
        } while (status == ErrorInsufficientBuffer && ++attempts < 5);
        if (status != 0)
        {
            Log.Warn($"Display HDR: QueryDisplayConfig failed with {status}.");
            return result;
        }
        for (var i = 0; i < numPaths; i++)
        {
            var path = paths[i];
            var name = GetSourceName(path.SourceInfo.AdapterId, path.SourceInfo.Id);
            if (name.Length == 0)
            {
                continue;
            }
            var info = new AdvancedColorInfo
            {
                Header =
                {
                    Type = GetAdvancedColorInfoType,
                    Size = (uint)sizeof(AdvancedColorInfo),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref info) != 0)
            {
                continue;
            }
            result.Add(new Target(name, path.TargetInfo.AdapterId, path.TargetInfo.Id,
                Supported: (info.Value & 1) != 0,
                Enabled: (info.Value & 2) != 0));
        }
        return result;
    }

    private static string GetSourceName(Luid adapterId, uint sourceId)
    {
        var packet = new SourceDeviceName
        {
            Header =
            {
                Type = GetSourceNameType,
                Size = (uint)sizeof(SourceDeviceName),
                AdapterId = adapterId,
                Id = sourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0)
        {
            return "";
        }
        var span = new ReadOnlySpan<char>(packet.ViewGdiDeviceName, 32);
        var end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }
}
