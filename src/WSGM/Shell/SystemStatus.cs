using System;
using System.Globalization;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Live system status for the quick access sheet's header: clock, date and
///     battery level (GetSystemPowerStatus). Refreshes on a 1 s UI-thread timer
///     while started; the sheet binds its status pills to this object.
///     Radio and audio state are not read here. They live on <see cref="Radios" />
///     and <see cref="Audio" />, which this object starts; the same manager
///     instances back the status pills and their panels, so each pair stays in sync.
/// </summary>
public sealed class SystemStatus : ObservableObject, IDisposable
{
    private bool _disposed;

    private long _formattedMinute = -1;
    private DispatcherTimer? _timer;

    /// <summary>
    ///     Creates a status cluster over managers owned by its composition.
    /// </summary>
    /// <param name="audio">
    ///     The composition's shared audio manager.
    /// </param>
    /// <param name="radios">
    ///     The composition's shared radio manager.
    /// </param>
    /// <param name="drives">
    ///     The composition's shared removable-drive manager.
    /// </param>
    /// <remarks>
    ///     The sheet comes and goes while a session lasts, so anything that must answer for the whole
    ///     session — Steam's audio namespace, in particular — cannot depend on a manager this object
    ///     disposes when the sheet closes. Sharing one instance rather than creating a second is the
    ///     point: two managers would enumerate endpoints twice and could disagree about which device is
    ///     default.
    /// </remarks>
    public SystemStatus(
        AudioManager audio, RadioManager radios, RemovableDriveManager drives)
    {
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        Radios = radios ?? throw new ArgumentNullException(nameof(radios));
        Drives = drives ?? throw new ArgumentNullException(nameof(drives));
    }

    /// <summary>Gets the current time of day, e.g. "21:37".</summary>
    public string ClockText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(ClockText));
    } = "";

    /// <summary>Gets the current date, e.g. "Fri 08 Aug" (localized day/month names).</summary>
    public string DateText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(DateText));
    } = "";

    /// <summary>
    ///     Gets whether a system battery with a known charge level exists; the
    ///     sheet hides the battery pill entirely when false (desktop PCs, or a
    ///     driver reporting the 255 unknown markers).
    /// </summary>
    public bool HasBattery
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(HasBattery));
    }

    /// <summary>Gets the battery charge in percent (0–100; 0 while <see cref="HasBattery" /> is false).</summary>
    public int BatteryPercent
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(BatteryPercent));
    }

    /// <summary>Gets the battery charge as display text, e.g. "87%" (empty without a battery).</summary>
    public string BatteryText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(BatteryText));
    } = "";

    /// <summary>
    ///     Gets the Wi-Fi and Bluetooth manager backing the sheet's radio
    ///     pills and the radio panel. Its lifetime belongs to the composition.
    /// </summary>
    public RadioManager Radios { get; }

    /// <summary>
    ///     Gets the master-volume and endpoint manager backing the sheet's
    ///     audio pill and audio panel. Its lifetime belongs to the composition.
    /// </summary>
    public AudioManager Audio { get; }

    /// <summary>
    ///     Gets the removable-storage manager backing the sheet's eject
    ///     pill and the Safe Eject panel. Its lifetime belongs to the composition.
    /// </summary>
    public RemovableDriveManager Drives { get; }

    /// <summary>
    ///     Stops this cluster's timers without disposing the supplied managers.
    ///     Idempotent; bound values keep their last state.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;

        Radios.Stop();

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>
    ///     Performs an immediate refresh and starts the 1 s update timer.
    ///     UI-thread callers only (the timer is a DispatcherTimer). Idempotent.
    ///     Refused after <see cref="Dispose" />; create a fresh cluster instead.
    /// </summary>
    public void Start()
    {
        if (_disposed)
        {
            Log.Warn("System status Start() ignored: the instance was already disposed.");
            return;
        }

        if (_timer is not null)
        {
            return;
        }

        Refresh();
        Radios.Start();
        Audio.Start();
        Drives.Start();
        Log.Info($"System status started (battery: {(HasBattery ? BatteryText : "none")}).");
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var minute = now.Ticks / TimeSpan.TicksPerMinute;
        if (minute != _formattedMinute)
        {
            // Both change at most once a minute; formatting them on every tick only allocates.
            _formattedMinute = minute;
            ClockText = FormatClock(now);
            DateText = FormatDate(now, CultureInfo.CurrentCulture);
        }

        var ok = WindowsPower.TryGetStatus(out var power);
        var (hasBattery, percent, text) = InterpretBattery(ok, power.BatteryFlag, power.BatteryLifePercent);
        HasBattery = hasBattery;
        BatteryPercent = percent;
        BatteryText = text;
    }

    /// <summary>Formats the sheet clock ("21:37"). 24-hour, culture-independent.</summary>
    internal static string FormatClock(DateTime now)
    {
        return now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats the sheet date ("Fri 08 Aug") with the culture's day/month names.</summary>
    internal static string FormatDate(DateTime now, CultureInfo culture)
    {
        return now.ToString("ddd dd MMM", culture);
    }

    /// <summary>
    ///     Maps a GetSystemPowerStatus result to the indicator state: hidden
    ///     (no battery / unknown markers) or a percent with display text.
    /// </summary>
    internal static (bool HasBattery, int Percent, string Text) InterpretBattery(
        bool callSucceeded, byte batteryFlag, byte lifePercent)
    {
        // The 0x80 mask covers both unknown markers (128 = no system battery,
        // 255 = unknown flag); 255 percent = unknown level.
        if (!callSucceeded || (batteryFlag & 0x80) != 0 || lifePercent > 100)
        {
            return (false, 0, "");
        }

        return (true, lifePercent, lifePercent + "%");
    }
}
