using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>Captures and applies per-monitor resolution, refresh-rate and DPI profiles.</summary>
public static unsafe partial class DisplayProfiles
{
    private const uint EnumCurrentSettings = 0xFFFFFFFF;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsTest = 0x00000002;
    private const uint CdsNoReset = 0x10000000;
    private const uint DisplayDeviceActive = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2;
        public uint PanningWidth, PanningHeight;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(char* device, uint index, ref DisplayDevice displayDevice, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettingsEx(char* deviceName, uint modeNum, ref DevMode devMode, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")]
    private static partial int ChangeDisplaySettingsEx(char* deviceName, DevMode* devMode, nint hwnd, uint flags, nint param);

    /// <summary>Captures the mode being left when automatic, then applies the mode being entered.</summary>
    public static void Transition(AppConfig config, bool enteringGameMode)
    {
        if (config.DisplayManagement == DisplayManagementMode.AutomaticProfiles)
        {
            Capture(config, game: !enteringGameMode);
            Persist(config);
        }
        Apply(config.DisplayProfiles, enteringGameMode);
    }

    /// <summary>Applies an already captured profile without taking a new snapshot.
    /// Recovery and uninstall paths use this because the current mode may be only
    /// partially initialized and must never overwrite the known-good profile.</summary>
    /// <param name="profiles">Per-monitor profiles to apply.</param>
    /// <param name="game">Whether to apply Game rather than Desktop values.</param>
    public static void ApplySaved(IEnumerable<MonitorDisplayProfile> profiles, bool game)
        => Apply(profiles, game);

    /// <summary>Returns current active display values for profile editing and automatic capture.</summary>
    public static List<MonitorDisplayProfile> ReadActiveProfiles()
    {
        var dpi = DisplayScale.ReadActivePercentages();
        var hdr = DisplayHdr.ReadActive();
        var result = new List<MonitorDisplayProfile>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(null, i, ref device, 0))
            {
                break;
            }
            if ((device.StateFlags & DisplayDeviceActive) == 0)
            {
                continue;
            }
            var name = FixedString(device.DeviceName, 32);
            var label = FixedString(device.DeviceString, 128);
            var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
            if (!EnumDisplaySettingsEx(device.DeviceName, EnumCurrentSettings, ref mode, 0))
            {
                continue;
            }
            var monitor = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            string monitorId = "";
            if (EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0))
            {
                monitorId = FixedString(monitor.DeviceKey, 128);
                label = FixedString(monitor.DeviceString, 128);
            }
            var hdrState = hdr.GetValueOrDefault(name);
            var values = new DisplayModeValues { Width = (int)mode.PelsWidth, Height = (int)mode.PelsHeight, RefreshRate = (int)mode.DisplayFrequency, DpiPercent = dpi.GetValueOrDefault(name, 100), HdrEnabled = hdrState.Enabled };
            result.Add(new MonitorDisplayProfile { MonitorId = monitorId, DeviceName = name, DisplayName = label, HdrAvailable = hdrState.Available, Desktop = Clone(values), Game = Clone(values) });
        }
        return result;
    }

    /// <summary>
    /// The refresh rates the primary display will actually accept at its current resolution.
    /// </summary>
    /// <returns>Accepted rates, ascending and deduplicated. Empty when the display cannot be read.</returns>
    /// <remarks>
    /// Enumerated and then <em>tested</em>, never assumed: a driver commonly offers rates the panel
    /// never advertises — the reference Claw accepts 30/48/60/75/100/120 while its EDID lists only
    /// 60 and 120 — and equally may refuse one it enumerated. `CDS_TEST` changes nothing, so this is
    /// safe to call while a game is running.
    /// <para>
    /// Hardcoding a rate list is the one thing this must never become: a panel without variable
    /// refresh will likely accept nothing but what it advertises, and that is exactly the case the
    /// frame-limit strategies exist to serve.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> EnumerateAcceptedRefreshRates()
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0))
        {
            Log.Warn("Display modes: current settings unreadable; no refresh rates discovered.");
            return [];
        }

        SortedSet<uint> enumerated = [];
        for (uint index = 0; ; index++)
        {
            var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
            if (!EnumDisplaySettingsEx(null, index, ref mode, 0))
            {
                break;
            }

            if (mode.PelsWidth == current.PelsWidth
                && mode.PelsHeight == current.PelsHeight
                && mode.BitsPerPel == current.BitsPerPel
                && mode.DisplayFrequency > 1)
            {
                enumerated.Add(mode.DisplayFrequency);
            }
        }

        List<int> accepted = [];
        List<uint> refused = [];
        foreach (uint hz in enumerated)
        {
            if (hz == current.DisplayFrequency || TestRefreshRate(current, hz))
            {
                accepted.Add((int)hz);
            }
            else
            {
                refused.Add(hz);
            }
        }

        Log.Info(
            $"Display modes: {current.PelsWidth}x{current.PelsHeight} at {current.DisplayFrequency} Hz, "
            + $"accepted [{string.Join(",", accepted)}]"
            + (refused.Count is 0 ? "" : $", refused [{string.Join(",", refused)}]"));
        return accepted;
    }

    /// <summary>
    /// Applies a refresh rate to the primary display without persisting it.
    /// </summary>
    /// <param name="refreshHz">The rate to apply.</param>
    /// <returns><see langword="true"/> when the display reports the new rate afterwards.</returns>
    /// <remarks>
    /// Deliberately dynamic: no `CDS_UPDATEREGISTRY`, so the user's saved display configuration is
    /// untouched and exit, a crash, or a reboot all restore it without WSGM doing anything. That is
    /// what makes a game-scoped refresh change safe to make at all.
    /// <para>
    /// Distinct from the display-profile path above, which deliberately does persist. Do not merge
    /// them: a profile is the user's chosen configuration, and this is a transient pairing WSGM owns
    /// for the duration of a cap.
    /// </para>
    /// </remarks>
    public static bool TryApplyTransientRefreshRate(int refreshHz)
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0))
        {
            Log.Warn($"Display modes: refusing {refreshHz} Hz; current settings unreadable.");
            return false;
        }

        if (current.DisplayFrequency == (uint)refreshHz)
        {
            return true;
        }

        var target = current;
        target.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        target.DisplayFrequency = (uint)refreshHz;
        int status = ChangeDisplaySettingsEx(null, &target, 0, 0, 0);
        if (status != 0)
        {
            Log.Warn(
                $"Display modes: {refreshHz} Hz refused with status {status} "
                + $"(was {current.DisplayFrequency} Hz).");
            return false;
        }

        Log.Info($"Display modes: {current.DisplayFrequency} Hz -> {refreshHz} Hz (transient).");
        return true;
    }

    private static bool TestRefreshRate(DevMode current, uint refreshHz)
    {
        var candidate = current;
        candidate.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        candidate.DisplayFrequency = refreshHz;
        return ChangeDisplaySettingsEx(null, &candidate, 0, CdsTest, 0) == 0;
    }

    private static void Capture(AppConfig config, bool game)
    {
        foreach (var current in ReadActiveProfiles())
        {
            var profile = config.DisplayProfiles.FirstOrDefault(p => SameMonitor(p, current));
            if (profile is null)
            {
                profile = current;
                config.DisplayProfiles.Add(profile);
            }
            var value = game ? current.Game : current.Desktop;
            if (game)
            {
                profile.Game = Clone(value);
            }
            else
            {
                profile.Desktop = Clone(value);
            }
            profile.MonitorId = current.MonitorId;
            profile.DeviceName = current.DeviceName;
            profile.DisplayName = current.DisplayName;
            profile.HdrAvailable = current.HdrAvailable;
            Log.Info($"Display profile captured ({(game ? "Game" : "Desktop")}): {current.DeviceName} {value.Width}x{value.Height} @ {value.RefreshRate} Hz, {value.DpiPercent}% DPI.");
        }
    }

    private static void Apply(IEnumerable<MonitorDisplayProfile> profiles, bool game)
    {
        var active = ReadActiveProfiles();
        var pending = new List<(MonitorDisplayProfile Profile, MonitorDisplayProfile Current, DisplayModeValues Value)>();
        foreach (var profile in profiles)
        {
            var current = active.FirstOrDefault(candidate => SameMonitor(profile, candidate));
            if (current is null)
            {
                continue;
            }
            var value = game ? profile.Game : profile.Desktop;
            if (value.Width <= 0 || value.Height <= 0 || value.RefreshRate <= 0)
            {
                continue;
            }
            var mode = CreateMode(value);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsTest, 0);
                if (status != 0)
                {
                    Log.Warn($"Display profile: {current.DeviceName} rejected {value.Width}x{value.Height} @ {value.RefreshRate} Hz ({status}); leaving every display mode unchanged.");
                    return;
                }
            }
            pending.Add((profile, current, value));
        }

        var changed = false;
        var staged = new List<MonitorDisplayProfile>();
        foreach (var (profile, current, value) in pending)
        {
            profile.DeviceName = current.DeviceName;
            var mode = CreateMode(value);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
                if (status == 0)
                {
                    changed = true;
                    staged.Add(current);
                    Log.Info($"Display profile staged ({(game ? "Game" : "Desktop")}): {current.DeviceName} {value.Width}x{value.Height} @ {value.RefreshRate} Hz.");
                }
                else
                {
                    Log.Warn($"Display profile: {profile.DeviceName} mode apply failed ({status}).");
                    RestoreStagedRegistry(staged);
                    return;
                }
            }
        }
        if (changed)
        {
            var status = ChangeDisplaySettingsEx(null, null, 0, 0, 0);
            if (status != 0)
            {
                Log.Warn($"Display profile: committing staged modes failed ({status}).");
                RestoreStagedRegistry(staged);
                return;
            }
        }
        DisplayScale.ApplyPercentages(profiles, game);
        DisplayHdr.Apply(profiles, game);
    }

    private static DevMode CreateMode(DisplayModeValues value)
        => new() { Size = (ushort)sizeof(DevMode), Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency, PelsWidth = (uint)value.Width, PelsHeight = (uint)value.Height, DisplayFrequency = (uint)value.RefreshRate };

    private static void RestoreStagedRegistry(IEnumerable<MonitorDisplayProfile> staged)
    {
        var restored = false;
        foreach (var current in staged)
        {
            var mode = CreateMode(current.Desktop);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
                if (status != 0)
                {
                    Log.Warn($"Display profile: rollback for {current.DeviceName} failed ({status}).");
                }
                else
                {
                    restored = true;
                }
            }
        }
        if (restored)
        {
            var status = ChangeDisplaySettingsEx(null, null, 0, 0, 0);
            if (status != 0)
            {
                Log.Warn($"Display profile: committing rollback failed ({status}).");
            }
        }
    }

    private static void Persist(AppConfig config) => ConfigStore.Mutate(fresh => fresh.DisplayProfiles = config.DisplayProfiles);
    private static bool SameMonitor(MonitorDisplayProfile left, MonitorDisplayProfile right)
        => !string.IsNullOrEmpty(left.MonitorId) && !string.IsNullOrEmpty(right.MonitorId)
            ? string.Equals(left.MonitorId, right.MonitorId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase);
    private static DisplayModeValues Clone(DisplayModeValues value) => new() { Width = value.Width, Height = value.Height, RefreshRate = value.RefreshRate, DpiPercent = value.DpiPercent, HdrEnabled = value.HdrEnabled };
    private static string FixedString(char* value, int length) { var span = new ReadOnlySpan<char>(value, length); var end = span.IndexOf('\0'); return new string(end < 0 ? span : span[..end]); }
}
