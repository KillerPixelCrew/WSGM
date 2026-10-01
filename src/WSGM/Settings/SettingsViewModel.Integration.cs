using System.Collections.Generic;
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

    /// <summary>The three detail options shared by every Custom-overlay widget selector.</summary>
    public List<string> OsdCustomLevels { get; } = ["Hidden", "Minimal", "Full"];

    /// <summary>Custom overlay (level 4) widget order, comma-separated widget names.</summary>
    public string OsdCustomOrder
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomOrder));
    }

    /// <summary>Clock detail for the Custom overlay.</summary>
    public int OsdCustomTimeIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomTimeIndex));
    }

    /// <summary>Framerate detail for the Custom overlay.</summary>
    public int OsdCustomFpsIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomFpsIndex));
    }

    /// <summary>CPU detail for the Custom overlay.</summary>
    public int OsdCustomCpuIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomCpuIndex));
    }

    /// <summary>Memory detail for the Custom overlay.</summary>
    public int OsdCustomRamIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomRamIndex));
    }

    /// <summary>GPU detail for the Custom overlay.</summary>
    public int OsdCustomGpuIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomGpuIndex));
    } = 2;

    /// <summary>Video-memory detail for the Custom overlay.</summary>
    public int OsdCustomVramIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomVramIndex));
    } = 2;

    /// <summary>Battery detail for the Custom overlay.</summary>
    public int OsdCustomBatteryIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomBatteryIndex));
    } = 2;

    /// <summary>Gets or sets how a frame cap is paired with the panel's refresh rate.</summary>
    /// <remarks>
    ///     Index into <see cref="FrameLimitStrategy" />, in declaration order, so the combo box needs no
    ///     converter. It decides both what the cap does to the display and which caps are offered at
    ///     all: uncoupled offers a free range, and the two coupled strategies offer only caps that
    ///     divide a real mode exactly.
    /// </remarks>
    public int FrameLimitStrategyIndex
    {
        get;
        set => SetField(ref field, value, nameof(FrameLimitStrategyIndex));
    }

    /// <summary>
    ///     Gets or sets the master Steam CEF integration switch. Off closes the
    ///     debug port, injects nothing, and hides the sub-toggles below and the overlay
    ///     feature buttons.
    /// </summary>
    public bool CefEnabled
    {
        get;
        set => SetField(ref field, value, nameof(CefEnabled));
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
        set => SetField(ref field, value, nameof(CefConnectedLibraryCarousel));
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
