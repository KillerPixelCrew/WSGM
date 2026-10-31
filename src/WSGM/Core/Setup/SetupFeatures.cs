namespace WSGM.Core;

/// <summary>The feature switches setup's profile page shows, one per line of its Customize panel.</summary>
public sealed record SetupFeatures
{
    /// <summary>WSGM hands controllers to Steam in games and takes them back for its own menus.</summary>
    public bool SteamInputManagement { get; init; }

    /// <summary>Steam Input lease around WSGM's own surfaces.</summary>
    public bool SteamInputLease { get; init; }

    /// <summary>Steam UI integration master switch.</summary>
    public bool SteamUi { get; init; }

    /// <summary>Library tabs per SD card.</summary>
    public bool LibraryTabs { get; init; }

    /// <summary>Card manager.</summary>
    public bool CardManager { get; init; }

    /// <summary>Connected-library carousel on Home.</summary>
    public bool ConnectedLibraryCarousel { get; init; }

    /// <summary>Format SD cards from Steam.</summary>
    public bool SdFormat { get; init; }

    /// <summary>Wi-Fi indicator in Big Picture's header.</summary>
    public bool WifiIndicator { get; init; }

    /// <summary>WSGM rows in Steam's Quick Access menu.</summary>
    public bool NativeQuickAccess { get; init; }

    /// <summary>Stay awake while Steam downloads.</summary>
    public bool DownloadKeepAwake { get; init; }

    /// <summary>Download queue sorting.</summary>
    public bool DownloadQueueSort { get; init; }

    /// <summary>Edge swipes: top opens WSGM, left and right open Steam's menus.</summary>
    public bool EdgeGestures { get; init; }

    /// <summary>The keyboard shortcut.</summary>
    public bool Hotkey { get; init; }

    /// <summary>The gamepad chord.</summary>
    public bool GamepadChord { get; init; }

    /// <summary>The boot splash over the desktop while Game Mode starts.</summary>
    public bool BootSplash { get; init; }

    /// <summary>
    ///     RTSS performance controls: frame limit, performance overlay and AutoTDP. Setup installs RTSS when this
    ///     is on and RTSS is missing.
    /// </summary>
    public bool Rtss { get; init; }

    /// <summary>Everything on.</summary>
    public static SetupFeatures Full { get; } = new()
    {
        SteamInputManagement = true, SteamInputLease = true, SteamUi = true, LibraryTabs = true,
        CardManager = true, ConnectedLibraryCarousel = true, SdFormat = true, WifiIndicator = true,
        NativeQuickAccess = true, DownloadKeepAwake = true, DownloadQueueSort = true, EdgeGestures = true,
        Hotkey = true, GamepadChord = true, BootSplash = true, Rtss = true
    };

    /// <summary>The WSGM session only: nothing in or around Steam, but WSGM stays reachable.</summary>
    public static SetupFeatures Minimal { get; } = new()
    {
        EdgeGestures = true, Hotkey = true, GamepadChord = true, BootSplash = true
    };
}
