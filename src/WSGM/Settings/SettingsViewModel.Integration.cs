using System;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Gets or sets the shared RTSS performance integration master switch.</summary>
    public bool PerformanceEnabled
    {
        get;
        set => SetField(ref field, value, nameof(PerformanceEnabled));
    }

    /// <summary>The custom overlay level's widgets, top to bottom, each with its detail.</summary>
    public ObservableCollection<OsdWidgetRow> OsdWidgets { get; } = [];

    /// <summary>The widgets the custom level knows, by their saved name, with what the rows call them.</summary>
    private static readonly (string Key, string Name)[] OsdWidgetNames =
    [
        ("Time", "Clock"), ("FPS", "Framerate"), ("CPU", "CPU"), ("GPU", "GPU"), ("RAM", "Memory"),
        ("VRAM", "Video memory"), ("BATT", "Battery")
    ];

    /// <summary>
    ///     Lists the widgets in their saved order. A widget the saved order leaves out does not render, so it is
    ///     listed last as hidden, which saves to the same overlay.
    /// </summary>
    private void LoadOsdWidgets(PerformanceConfig performance)
    {
        var saved = RtssOsdCustomSettings.FromConfig(performance);
        OsdWidgets.Clear();
        foreach (var canonical in saved.Order)
        {
            var (key, name) = OsdWidgetNames.First(widget => widget.Key.Equals(canonical, StringComparison.OrdinalIgnoreCase));
            OsdWidgets.Add(new OsdWidgetRow(key, name, LevelOf(saved, key), OsdWidgetsChanged));
        }

        foreach (var (key, name) in OsdWidgetNames.Where(widget =>
                     !saved.Order.Contains(widget.Key.ToUpperInvariant())))
        {
            OsdWidgets.Add(new OsdWidgetRow(key, name, 0, OsdWidgetsChanged));
        }
    }

    /// <summary>Moves a widget up (negative) or down (positive), stopping at either end.</summary>
    /// <param name="row">The widget.</param>
    /// <param name="offset">How many places to move it.</param>
    /// <returns>Whether it moved.</returns>
    internal bool MoveOsdWidget(OsdWidgetRow row, int offset)
    {
        var from = OsdWidgets.IndexOf(row);
        var to = Math.Clamp(from + offset, 0, OsdWidgets.Count - 1);
        if (from < 0 || from == to)
        {
            return false;
        }

        OsdWidgets.Move(from, to);
        OsdWidgetsChanged();
        return true;
    }

    private void OsdWidgetsChanged()
    {
        Raise(nameof(OsdWidgets));
    }

    /// <summary>Writes the widget order and every widget's detail.</summary>
    private void SaveOsdWidgets(PerformanceConfig performance)
    {
        performance.OsdCustomOrder = string.Join(",", OsdWidgets.Select(static row => row.Key));
        int Level(string key) => OsdWidgets.First(row => row.Key == key).Level;
        performance.OsdCustomTime = Level("Time");
        performance.OsdCustomFps = Level("FPS");
        performance.OsdCustomCpu = Level("CPU");
        performance.OsdCustomGpu = Level("GPU");
        performance.OsdCustomRam = Level("RAM");
        performance.OsdCustomVram = Level("VRAM");
        performance.OsdCustomBattery = Level("BATT");
    }

    private static int LevelOf(RtssOsdCustomSettings saved, string key)
    {
        return key switch
        {
            "Time" => saved.Time,
            "FPS" => saved.Fps,
            "CPU" => saved.Cpu,
            "GPU" => saved.Gpu,
            "RAM" => saved.Ram,
            "VRAM" => saved.Vram,
            _ => saved.Battery
        };
    }

    /// <summary>Gets whether the carousel's sub-option shows: its feature is on, and so is the master switch.</summary>
    public bool ShowCarouselUninstalledRow => CefEnabled && CefConnectedLibraryCarousel;

    /// <summary>
    ///     Gets or sets the master Steam CEF integration switch. Off closes the
    ///     debug port, injects nothing, and hides the sub-toggles below and the overlay
    ///     feature buttons.
    /// </summary>
    public bool CefEnabled
    {
        get;
        set
        {
            SetField(ref field, value, nameof(CefEnabled));
            Raise(nameof(ShowCarouselUninstalledRow));
        }
    } = true;

    /// <summary>Gets or sets the injected library filter tabs, tab order, and native-tab hiding.</summary>
    public bool CefLibraryTabs
    {
        get;
        set => SetField(ref field, value, nameof(CefLibraryTabs));
    } = true;

    /// <summary>Gets or sets the SD-card library manager (card tabs, badges, live labels).</summary>
    public bool CefCardManager
    {
        get;
        set => SetField(ref field, value, nameof(CefCardManager));
    } = true;

    /// <summary>Gets or sets Format SD Card + live library registration.</summary>
    public bool CefSdFormat
    {
        get;
        set => SetField(ref field, value, nameof(CefSdFormat));
    } = true;

    /// <summary>Gets or sets whether Steam's own storage pages may erase a drive through WSGM.</summary>
    /// <remarks>
    ///     An opt-out, separate from <see cref="CefSdFormat" />: that one is WSGM's own guided flow,
    ///     this one lets Steam's Format Drive modal start the same erase. The refusal Steam shows when
    ///     this is off is a generic result code, so the switch has to be where the user can find it —
    ///     which it was not, for a day.
    /// </remarks>
    public bool SteamStorageFormat
    {
        get;
        set => SetField(ref field, value, nameof(SteamStorageFormat));
    }

    /// <summary>Gets or sets the Big Picture Wi-Fi indicator.</summary>
    public bool CefWifiIndicator
    {
        get;
        set => SetField(ref field, value, nameof(CefWifiIndicator));
    } = true;

    /// <summary>Gets or sets the fingerprint-gated native Steam Quick Access bootstrap.</summary>
    public bool CefNativeQuickAccess
    {
        get;
        set => SetField(ref field, value, nameof(CefNativeQuickAccess));
    } = true;

    /// <summary>
    ///     Gets or sets the automatic download wake lock (keep the device awake
    ///     while Steam reports an active download).
    /// </summary>
    public bool CefDownloadKeepAwake
    {
        get;
        set => SetField(ref field, value, nameof(CefDownloadKeepAwake));
    } = true;

    /// <summary>
    ///     Gets or sets the Name/Size/Type sort buttons injected into Big
    ///     Picture's download-queue header.
    /// </summary>
    public bool CefDownloadQueueSort
    {
        get;
        set => SetField(ref field, value, nameof(CefDownloadQueueSort));
    } = true;

    /// <summary>
    ///     Gets or sets whether Big Picture Home's carousel lists the games on the
    ///     libraries attached right now.
    /// </summary>
    public bool CefConnectedLibraryCarousel
    {
        get;
        set
        {
            SetField(ref field, value, nameof(CefConnectedLibraryCarousel));
            Raise(nameof(ShowCarouselUninstalledRow));
        }
    } = true;

    /// <summary>
    ///     Gets or sets whether that carousel also lists owned games that are not
    ///     installed, greyed.
    /// </summary>
    public bool CefCarouselShowUninstalled
    {
        get;
        set => SetField(ref field, value, nameof(CefCarouselShowUninstalled));
    }
}
