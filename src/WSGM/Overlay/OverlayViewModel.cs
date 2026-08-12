using System.ComponentModel;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>State for the overlay, recomputed every time it is shown.</summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after an overlay property or dependent display value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _explorerRunning;
    private bool _homeAppAlive;
    private string _homeAppName = "Home app";
    private string _warningText = "";
    private GlyphStyle _glyphStyle = GlyphStyle.Xbox;

    /// <summary>Gets or sets whether Explorer is currently running.</summary>
    public bool ExplorerRunning
    {
        get => _explorerRunning;
        set { _explorerRunning = value; Raise(nameof(ExplorerRunning)); Raise(nameof(DesktopButtonText)); }
    }

    /// <summary>Gets or sets whether the configured home application has a live process.</summary>
    public bool HomeAppAlive
    {
        get => _homeAppAlive;
        set { _homeAppAlive = value; Raise(nameof(HomeAppAlive)); Raise(nameof(HomeAppButtonText)); }
    }

    /// <summary>Gets or sets the configured home application's display name.</summary>
    public string HomeAppName
    {
        get => _homeAppName;
        set { _homeAppName = value; Raise(nameof(HomeAppName)); Raise(nameof(HomeAppButtonText)); Raise(nameof(CloseLauncherText)); }
    }

    /// <summary>Gets or sets the non-fatal warning displayed by the overlay.</summary>
    public string WarningText
    {
        get => _warningText;
        set { _warningText = value; Raise(nameof(WarningText)); Raise(nameof(HasWarning)); }
    }

    /// <summary>Gets whether a warning should be rendered.</summary>
    public bool HasWarning => _warningText.Length > 0;

    /// <summary>Gets or sets the controller glyph family used by the overlay.</summary>
    public GlyphStyle GlyphStyle
    {
        get => _glyphStyle;
        set { _glyphStyle = value; Raise(nameof(GlyphStyle)); }
    }

    private bool _confirmingCloseLauncher;
    /// <summary>Armed state of the destructive Close-Steam confirm. Lives here so
    /// the bound title renders it — a direct Text write would fight the binding.</summary>
    public bool ConfirmingCloseLauncher
    {
        get => _confirmingCloseLauncher;
        set { _confirmingCloseLauncher = value; Raise(nameof(ConfirmingCloseLauncher)); Raise(nameof(CloseLauncherText)); }
    }

    /// <summary>Gets the action label that switches between desktop and game mode.</summary>
    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";

    /// <summary>Gets the action label that starts or focuses the home application.</summary>
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";

    /// <summary>Gets the destructive-action label, including confirmation state.</summary>
    public string CloseLauncherText => ConfirmingCloseLauncher ? "Really?" : $"Close {HomeAppName}";

    private ManualWakeMode _keepAwakeManualMode = ManualWakeMode.Off;
    private bool _keepAwakeDownload;
    private string _wakeLockSummary = "";

    /// <summary>Whether the keep-awake row is shown at all (a session
    /// <c>KeepAwakeService</c> exists; the Settings preview overlay has none).</summary>
    public bool ShowKeepAwake { get; init; }

    /// <summary>The user's manual wake mode (the row cycles it).</summary>
    public ManualWakeMode KeepAwakeManualMode
    {
        get => _keepAwakeManualMode;
        set
        {
            _keepAwakeManualMode = value;
            Raise(nameof(KeepAwakeManualMode));
            Raise(nameof(KeepAwakeDescription));
            Raise(nameof(KeepAwakeTrailing));
        }
    }

    /// <summary>Whether the automatic download keep-awake hold is active.</summary>
    public bool KeepAwakeDownloadActive
    {
        get => _keepAwakeDownload;
        set
        {
            _keepAwakeDownload = value;
            Raise(nameof(KeepAwakeDownloadActive));
            Raise(nameof(KeepAwakeDescription));
            Raise(nameof(KeepAwakeTrailing));
        }
    }

    /// <summary>System-wide wake-lock holder summary from the indicator poll
    /// (for example "Standby blocked by steam.exe ×3"); empty when free, unknown,
    /// or when only WSGM itself holds locks.</summary>
    public string WakeLockSummary
    {
        get => _wakeLockSummary;
        set
        {
            _wakeLockSummary = value;
            Raise(nameof(WakeLockSummary));
            Raise(nameof(KeepAwakeDescription));
        }
    }

    /// <summary>Gets the status line rendered under the keep-awake row: WSGM's own
    /// mode first, then other holders seen by the indicator, then the cycle hint.</summary>
    public string KeepAwakeDescription => KeepAwakeManualMode switch
    {
        ManualWakeMode.StandbyAndDisplay => "Standby blocked and screen kept on",
        ManualWakeMode.Standby => "Standby blocked until you turn this off",
        _ when KeepAwakeDownloadActive => "Held awake while Steam downloads",
        _ when WakeLockSummary.Length > 0 => WakeLockSummary,
        _ => "Off",
    };

    /// <summary>Gets the trailing badge for the keep-awake row ("ON" for a standby
    /// hold, "ON+" when the display is pinned too, empty otherwise).</summary>
    public string KeepAwakeTrailing => KeepAwakeManualMode switch
    {
        ManualWakeMode.StandbyAndDisplay => "ON+",
        ManualWakeMode.Standby => "ON",
        _ => KeepAwakeDownloadActive ? "ON" : "",
    };

    private string _displayDcTimeout = "—";
    private string _displayAcTimeout = "—";
    private string _sleepDcTimeout = "—";
    private string _sleepAcTimeout = "—";

    /// <summary>Current display-off timeout on battery, as a trailing badge ("5 min",
    /// "Never", "—" when the power API gave no answer).</summary>
    public string DisplayDcTimeout
    {
        get => _displayDcTimeout;
        set { _displayDcTimeout = value; Raise(nameof(DisplayDcTimeout)); }
    }

    /// <summary>Current display-off timeout when plugged in.</summary>
    public string DisplayAcTimeout
    {
        get => _displayAcTimeout;
        set { _displayAcTimeout = value; Raise(nameof(DisplayAcTimeout)); }
    }

    /// <summary>Current standby timeout on battery.</summary>
    public string SleepDcTimeout
    {
        get => _sleepDcTimeout;
        set { _sleepDcTimeout = value; Raise(nameof(SleepDcTimeout)); }
    }

    /// <summary>Current standby timeout when plugged in.</summary>
    public string SleepAcTimeout
    {
        get => _sleepAcTimeout;
        set { _sleepAcTimeout = value; Raise(nameof(SleepAcTimeout)); }
    }

    private bool _showLibraryTabs = true;
    private bool _showCardManager = true;
    private bool _showArtwork = true;
    private bool _showSdCard = true;
    private bool _configureLaunchOptionsLive = true;

    // Settable, not init-only: a config saved from another process while the panel
    // is open must be able to hide a feature the user just turned off, instead of
    // leaving a button that drives a now-disabled integration until the next reopen.

    /// <summary>Whether the CEF library-tabs builder button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.LibraryTabs</c>). A hidden button removes the
    /// only entry point to that CEF feature.</summary>
    public bool ShowLibraryTabs
    {
        get => _showLibraryTabs;
        set { _showLibraryTabs = value; Raise(nameof(ShowLibraryTabs)); Raise(nameof(ShowSteamLibrarySection)); }
    }

    /// <summary>Whether the CEF SD-card library-manager button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.CardManager</c>).</summary>
    public bool ShowCardManager
    {
        get => _showCardManager;
        set { _showCardManager = value; Raise(nameof(ShowCardManager)); Raise(nameof(ShowSteamLibrarySection)); }
    }

    /// <summary>Whether the CEF shortcut-artwork button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.Artwork</c>).</summary>
    public bool ShowArtwork
    {
        get => _showArtwork;
        set { _showArtwork = value; Raise(nameof(ShowArtwork)); Raise(nameof(ShowSteamLibrarySection)); }
    }

    /// <summary>Whether the CEF Format-SD-card and Add-library buttons are shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.SdFormat</c>).</summary>
    public bool ShowSdCard
    {
        get => _showSdCard;
        set { _showSdCard = value; Raise(nameof(ShowSdCard)); Raise(nameof(ShowSteamLibrarySection)); }
    }

    /// <summary>Whether the "STEAM LIBRARY" tools section has any visible button, so
    /// its header is hidden rather than left orphaned when every CEF feature is off.</summary>
    public bool ShowSteamLibrarySection =>
        ShowLibraryTabs || ShowCardManager || ShowArtwork || ShowSdCard;

    /// <summary>Whether the launch-wrapper buttons configure the selected game in the
    /// running Steam client (<c>Cef.Enabled</c>) instead of copying a command to the
    /// clipboard for the user to paste by hand.</summary>
    public bool ConfigureLaunchOptionsLive
    {
        get => _configureLaunchOptionsLive;
        set
        {
            _configureLaunchOptionsLive = value;
            Raise(nameof(ConfigureLaunchOptionsLive));
            Raise(nameof(ShowRemoveLaunchWrapper));
        }
    }

    /// <summary>Whether the "remove wrappers" row is shown — only meaningful when WSGM
    /// can write the launch configuration itself.</summary>
    public bool ShowRemoveLaunchWrapper => ConfigureLaunchOptionsLive;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
