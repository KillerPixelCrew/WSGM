using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>Per-monitor display scaling (the 100%/125%/150% setting) via the
/// undocumented-but-ABI-stable DisplayConfig DPI packets (types -3/-4) — the same
/// mechanism the Settings app uses. Applies INSTANTLY, no logoff (live-verified),
/// and PERSISTS in the registry — which is why the pre-game values are stored in
/// WSGM's config and restored on desktop mode, clean exit, panic, and recovery.
/// Game mode runs at 100% so DPI-unaware games render 1:1 on the panel.</summary>
public static partial class DisplayScale
{
    private const int GetDpiScaleType = -3;
    private const int SetDpiScaleType = -4;
    private const uint QdcOnlyActivePaths = 0x00000002;

    // Index 0 = 100%. Recommended = DpiVals[abs(MinScaleRel)].
    private static readonly uint[] DpiVals = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;             // SOURCE id, not target
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet     // 0x20 bytes; field order min,cur,max (verified)
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet     // 0x18 bytes
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }

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
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] PathInfo[] paths,
        ref uint numModes, [In, Out] ModeInfo[] modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref DpiScaleGet packet);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigSetDeviceInfo(ref DpiScaleSet packet);

    /// <summary>Game mode: capture the current per-display scaling into the config
    /// (unless a crashed session already left captured values there) and drop
    /// every display to 100%. Saves the config when values were captured.</summary>
    public static void ApplyGameMode(AppConfig config)
    {
        var sources = GetActiveSources();
        if (sources.Count == 0)
        {
            Log.Warn("Display scale: no active display sources found.");
            return;
        }

        var freshCapture = config.SavedDisplayScales.Count == 0;
        var captured = new List<int>();
        foreach (var source in sources)
        {
            if (!TryGetScale(source, out var current, out _, out _))
            {
                captured.Add(0);
                continue;
            }
            captured.Add((int)current);
            if (current != 100)
            {
                TrySetScale(source, 100);
                Log.Info($"Display scale -> 100% (was {current}%).");
            }
        }

        if (freshCapture && captured.Exists(v => v != 0 && v != 100))
        {
            config.SavedDisplayScales = captured;
            try { ConfigStore.Save(config); } catch (Exception ex) { Log.Warn($"Display scale: could not persist saved values: {ex.Message}"); }
        }
    }

    /// <summary>Restores the captured scaling (desktop mode, clean exit, panic,
    /// recovery). Clears and persists the config when something was restored.</summary>
    public static void RestoreSaved(AppConfig config)
    {
        if (config.SavedDisplayScales.Count == 0)
        {
            return;
        }
        var sources = GetActiveSources();
        for (var i = 0; i < sources.Count && i < config.SavedDisplayScales.Count; i++)
        {
            var target = config.SavedDisplayScales[i];
            if (target is >= 100 and <= 500)
            {
                TrySetScale(sources[i], (uint)target);
                Log.Info($"Display scale restored to {target}%.");
            }
        }
        config.SavedDisplayScales = [];
        try { ConfigStore.Save(config); } catch (Exception ex) { Log.Warn($"Display scale: could not persist restore: {ex.Message}"); }
    }

    private static bool TryGetScale((Luid Adapter, uint SourceId) source, out uint currentPct, out uint recommendedPct, out uint maxPct)
    {
        currentPct = recommendedPct = maxPct = 0;
        var get = new DpiScaleGet
        {
            Header =
            {
                Type = GetDpiScaleType,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                AdapterId = source.Adapter,
                Id = source.SourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref get) != 0)
        {
            return false;
        }
        var cur = Math.Clamp(get.CurScaleRel, get.MinScaleRel, get.MaxScaleRel);
        var rec = Math.Abs(get.MinScaleRel);
        if (rec + get.MaxScaleRel + 1 > DpiVals.Length)
        {
            return false;
        }
        currentPct = DpiVals[rec + cur];
        recommendedPct = DpiVals[rec];
        maxPct = DpiVals[rec + get.MaxScaleRel];
        return true;
    }

    private static bool TrySetScale((Luid Adapter, uint SourceId) source, uint percent)
    {
        if (!TryGetScale(source, out var current, out var recommended, out var max))
        {
            return false;
        }
        if (percent == current)
        {
            return true;
        }
        percent = Math.Clamp(percent, 100u, max);
        var idx = Array.IndexOf(DpiVals, percent);
        var recIdx = Array.IndexOf(DpiVals, recommended);
        if (idx < 0 || recIdx < 0)
        {
            return false;
        }
        var set = new DpiScaleSet
        {
            Header =
            {
                Type = SetDpiScaleType,
                Size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                AdapterId = source.Adapter,
                Id = source.SourceId,
            },
            ScaleRel = idx - recIdx,
        };
        var ok = DisplayConfigSetDeviceInfo(ref set) == 0;
        if (!ok)
        {
            Log.Warn($"Display scale: set {percent}% failed.");
        }
        return ok;
    }

    private static List<(Luid Adapter, uint SourceId)> GetActiveSources()
    {
        var result = new List<(Luid, uint)>();
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var numPaths, out var numModes) != 0)
            {
                return result;
            }
            var paths = new PathInfo[numPaths];
            var modes = new ModeInfo[numModes];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, 0) != 0)
            {
                return result;
            }
            for (var i = 0; i < numPaths; i++)
            {
                var key = (paths[i].SourceInfo.AdapterId, paths[i].SourceInfo.Id);
                if (!result.Exists(x => x.Item2 == key.Id
                    && x.Item1.LowPart == key.AdapterId.LowPart && x.Item1.HighPart == key.AdapterId.HighPart))
                {
                    result.Add(key);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: enumeration failed: {ex.Message}");
        }
        return result;
    }
}
