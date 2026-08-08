using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Live system status for the game-mode taskbar's right zone: clock, date,
/// battery level (GetSystemPowerStatus) and best-effort Wi-Fi state (flat wlanapi,
/// read-only). Refreshes on a 1 s UI-thread timer while started; the taskbar binds
/// its status cluster to this object. Bluetooth soft-radio state has no cheap
/// COM/WinRT-free Win32 API, so it stays "State unavailable" and its button renders
/// neutral — the flyouts are forward-prep for a hand-rolled radio manager.</summary>
public sealed class SystemStatus : INotifyPropertyChanged, IDisposable
{
    /// <summary>Raised after a status property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private DispatcherTimer? _timer;
    private int _ticks;
    private bool _wifiFailureLogged;

    private string _clockText = "";
    /// <summary>Gets the current time of day, e.g. "21:37".</summary>
    public string ClockText
    {
        get => _clockText;
        private set => Set(ref _clockText, value, nameof(ClockText));
    }

    private string _dateText = "";
    /// <summary>Gets the current date, e.g. "Fri 08 Aug" (localized day/month names).</summary>
    public string DateText
    {
        get => _dateText;
        private set => Set(ref _dateText, value, nameof(DateText));
    }

    private bool _hasBattery;
    /// <summary>Gets whether a system battery with a known charge level exists; the
    /// taskbar hides the battery indicator entirely when false (desktop PCs, or a
    /// driver reporting the 255 unknown markers).</summary>
    public bool HasBattery
    {
        get => _hasBattery;
        private set => Set(ref _hasBattery, value, nameof(HasBattery));
    }

    private int _batteryPercent;
    /// <summary>Gets the battery charge in percent (0–100; 0 while <see cref="HasBattery"/> is false).</summary>
    public int BatteryPercent
    {
        get => _batteryPercent;
        private set
        {
            if (_batteryPercent != value)
            {
                _batteryPercent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPercent)));
            }
        }
    }

    private string _batteryText = "";
    /// <summary>Gets the battery charge as display text, e.g. "87%" (empty without a battery).</summary>
    public string BatteryText
    {
        get => _batteryText;
        private set => Set(ref _batteryText, value, nameof(BatteryText));
    }

    private bool _wifiConnected;
    /// <summary>Gets whether Wi-Fi is currently connected to a network — the only
    /// state that tints the taskbar's Wi-Fi button with the accent color.</summary>
    public bool WifiConnected
    {
        get => _wifiConnected;
        private set => Set(ref _wifiConnected, value, nameof(WifiConnected));
    }

    private string _wifiStateText = "State unavailable";
    /// <summary>Gets the Wi-Fi state line for the button's flyout: "Connected",
    /// "Not connected", or "State unavailable" when the state cannot be read.</summary>
    public string WifiStateText
    {
        get => _wifiStateText;
        private set => Set(ref _wifiStateText, value, nameof(WifiStateText));
    }

    /// <summary>Gets the Bluetooth state line for the button's flyout. Always
    /// "State unavailable": reading the soft-radio state requires WinRT/COM, which
    /// this NativeAOT build deliberately excludes.</summary>
    public string BluetoothStateText => "State unavailable";

    /// <summary>Performs an immediate refresh and starts the 1 s update timer.
    /// UI-thread callers only (the timer is a DispatcherTimer). Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }
        Refresh(refreshWifi: true);
        Log.Info($"System status started (battery: {(HasBattery ? BatteryText : "none")}, Wi-Fi: {WifiStateText}).");
        // Parameterless ctor + explicit Start (CLAUDE.md invariant 4).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the update timer. Idempotent; bound values keep their last state.</summary>
    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // The Wi-Fi read opens a WLAN handle — every 5th tick is plenty for a
        // status tint, the clock and battery are cheap enough for every second.
        _ticks++;
        Refresh(refreshWifi: _ticks % 5 == 0);
    }

    private void Refresh(bool refreshWifi)
    {
        var now = DateTime.Now;
        ClockText = FormatClock(now);
        DateText = FormatDate(now, CultureInfo.CurrentCulture);

        var ok = NativeMethods.GetSystemPowerStatus(out var power);
        var (hasBattery, percent, text) = InterpretBattery(ok, power.BatteryFlag, power.BatteryLifePercent);
        HasBattery = hasBattery;
        BatteryPercent = percent;
        BatteryText = text;

        if (refreshWifi)
        {
            var state = QueryWifiState();
            WifiConnected = state == WifiState.Connected;
            WifiStateText = DescribeWifi(state);
        }
    }

    /// <summary>Best-effort Wi-Fi adapter state.</summary>
    internal enum WifiState
    {
        /// <summary>No adapter, the WLAN service is unavailable, or the query failed.</summary>
        Unknown,

        /// <summary>An adapter exists but is not connected to a network.</summary>
        Disconnected,

        /// <summary>Connected to a network.</summary>
        Connected,
    }

    /// <summary>Formats the taskbar clock ("21:37"). 24-hour, culture-independent.</summary>
    internal static string FormatClock(DateTime now)
        => now.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Formats the taskbar date ("Fri 08 Aug") with the culture's day/month names.</summary>
    internal static string FormatDate(DateTime now, CultureInfo culture)
        => now.ToString("ddd dd MMM", culture);

    /// <summary>Maps a GetSystemPowerStatus result to the indicator state: hidden
    /// (no battery / unknown markers) or a percent with display text.</summary>
    internal static (bool HasBattery, int Percent, string Text) InterpretBattery(
        bool callSucceeded, byte batteryFlag, byte lifePercent)
    {
        // 128 = no system battery, 255 = unknown flag; 255 percent = unknown level.
        if (!callSucceeded || (batteryFlag & 0x80) != 0 || batteryFlag == 255 || lifePercent > 100)
        {
            return (false, 0, "");
        }
        return (true, lifePercent, lifePercent + "%");
    }

    /// <summary>The flyout's state line for a Wi-Fi query result.</summary>
    internal static string DescribeWifi(WifiState state) => state switch
    {
        WifiState.Connected => "Connected",
        WifiState.Disconnected => "Not connected",
        _ => "State unavailable",
    };

    /// <summary>Interprets a WlanEnumInterfaces result buffer. Scans EVERY
    /// fixed-size interface record and reports Connected when ANY interface is
    /// connected — a disconnected onboard adapter must not mask a connected USB
    /// adapter.</summary>
    /// <param name="list">A non-null WLAN_INTERFACE_INFO_LIST allocation.</param>
    internal static WifiState ReadInterfaceList(nint list)
    {
        // WLAN_INTERFACE_INFO_LIST header: dwNumberOfItems + dwIndex (8 bytes), then
        // dwNumberOfItems packed WLAN_INTERFACE_INFO records:
        // GUID (16) + WCHAR[256] description (512) + isState (4) = 532 bytes each.
        const int HeaderSize = 8;
        const int StateOffset = 16 + 512;
        const int RecordSize = StateOffset + 4;
        var count = Marshal.ReadInt32(list);
        if (count <= 0)
        {
            return WifiState.Unknown;
        }
        for (var i = 0; i < count; i++)
        {
            var isState = Marshal.ReadInt32(list, HeaderSize + (i * RecordSize) + StateOffset);
            if (isState == NativeMethods.WlanInterfaceStateConnected)
            {
                return WifiState.Connected;
            }
        }
        return WifiState.Disconnected;
    }

    /// <summary>Reads the WLAN interfaces' state via the flat wlanapi (no
    /// COM/WinRT); connected when any interface is connected. Any failure —
    /// service down, no adapter, missing DLL — degrades to Unknown, which
    /// renders the button neutral.</summary>
    private WifiState QueryWifiState()
    {
        try
        {
            if (NativeMethods.WlanOpenHandle(2, 0, out _, out var client) != 0)
            {
                return WifiState.Unknown;
            }
            try
            {
                if (NativeMethods.WlanEnumInterfaces(client, 0, out var list) != 0 || list == 0)
                {
                    return WifiState.Unknown;
                }
                try
                {
                    return ReadInterfaceList(list);
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(list);
                }
            }
            finally
            {
                _ = NativeMethods.WlanCloseHandle(client, 0);
            }
        }
        catch (Exception ex)
        {
            if (!_wifiFailureLogged)
            {
                _wifiFailureLogged = true;
                Log.Warn($"Wi-Fi state query failed; rendering neutral: {ex.Message}");
            }
            return WifiState.Unknown;
        }
    }

    private void Set(ref string field, string value, string name)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
