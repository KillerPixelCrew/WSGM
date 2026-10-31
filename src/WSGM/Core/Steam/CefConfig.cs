namespace WSGM.Core;

/// <summary>
///     Master switch and per-feature sub-toggles for WSGM's Steam CEF
///     (Chromium Embedded Framework) integration — everything WSGM injects into Steam
///     over its debug port. <see cref="Enabled" /> off means WSGM never writes or uses
///     the CEF debug flag at all (no injection, and the sub-features are hidden from
///     the overlay); the sub-toggles gate individual injected features while CEF is on.
///     Every feature flag defaults on, so an existing install behaves exactly as before; the one
///     presentation preference, <see cref="CarouselShowUninstalled" />, defaults off.
/// </summary>
public sealed class CefConfig
{
    /// <summary>
    ///     Master CEF switch. Off = the debug-port flag is never written, no
    ///     injection is attempted, and every CEF feature below is hidden.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Injected library filter tabs plus the tab-strip order and native-tab
    ///     hiding — one subsystem (<c>SteamLibraryTabs</c>).
    /// </summary>
    public bool LibraryTabs { get; set; } = true;

    /// <summary>
    ///     The SD-card library manager: per-card injected library tabs, the
    ///     library badge on Steam's game tiles, and live library relabeling.
    /// </summary>
    public bool CardManager { get; set; } = true;

    /// <summary>
    ///     Big Picture Home's carousel shows the games on the libraries attached right
    ///     now, newest played first, behind recent purchases, instead of Steam's own mix
    ///     (<c>SteamHomeCarouselSurface</c>).
    /// </summary>
    public bool ConnectedLibraryCarousel { get; set; } = true;

    /// <summary>
    ///     With <see cref="ConnectedLibraryCarousel" /> on, also lists owned games that are
    ///     not installed, greyed. A presentation preference rather than a feature, so it defaults
    ///     off.
    /// </summary>
    public bool CarouselShowUninstalled { get; set; }

    /// <summary>
    ///     Format SD Card and register its library into the running Steam. The
    ///     whole feature (native disk format included) is hidden when off.
    /// </summary>
    public bool SdFormat { get; set; } = true;

    /// <summary>Big Picture header Wi-Fi indicator (feeds Steam's <c>SystemNetworkStore</c>).</summary>
    public bool WifiIndicator { get; set; } = true;

    /// <summary>
    ///     Narrow, fingerprint-gated native Quick Access bootstrap over the persistent Steam UI host.
    /// </summary>
    public bool NativeQuickAccess { get; set; } = true;

    /// <summary>
    ///     Automatic wake lock while the running Steam client reports an active
    ///     download (polled over the CEF bridge), so the device finishes downloading
    ///     instead of entering standby. The quick-access Power tab's manual Keep Awake
    ///     cycle works regardless of this flag.
    /// </summary>
    public bool DownloadKeepAwake { get; set; } = true;

    /// <summary>
    ///     Name/Size/Type sort buttons injected into the header of Big Picture's
    ///     download queue (<c>SteamDownloadSort</c>).
    /// </summary>
    public bool DownloadQueueSort { get; set; } = true;
}
