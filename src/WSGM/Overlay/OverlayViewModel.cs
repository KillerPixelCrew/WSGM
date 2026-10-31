using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>State for the overlay, recomputed every time it is shown.</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private string _warningText = "";

    /// <summary>Whether the independent emulator tool is available in this session.</summary>
    public bool ShowEmulators
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowEmulators));
    }

    internal IReadOnlyDictionary<PowerTimeoutKind, int?> PowerTimeoutMinimums { get; set; } =
        new Dictionary<PowerTimeoutKind, int?>();

    internal IReadOnlyDictionary<PowerTimeoutKind, int?> PowerTimeoutValues
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(PowerTimeoutValues));
    } = new Dictionary<PowerTimeoutKind, int?>();

    /// <summary>Gets or sets whether Explorer is currently running.</summary>
    public bool ExplorerRunning
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(ExplorerRunning)))
            {
                Raise(nameof(DesktopButtonText));
            }
        }
    }

    /// <summary>Gets or sets whether the configured home application has a live process.</summary>
    public bool HomeAppAlive
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(HomeAppAlive)))
            {
                Raise(nameof(HomeAppButtonText));
            }
        }
    }

    /// <summary>Gets or sets the configured home application's display name.</summary>
    public string HomeAppName
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(HomeAppName)))
            {
                return;
            }

            Raise(nameof(HomeAppButtonText));
            Raise(nameof(CloseLauncherText));
        }
    } = "Home app";

    /// <summary>Gets or sets the non-fatal warning displayed by the overlay.</summary>
    public string WarningText
    {
        get => _warningText;
        set
        {
            if (SetFieldIfChanged(ref _warningText, value, nameof(WarningText)))
            {
                Raise(nameof(HasWarning));
            }
        }
    }

    /// <summary>Gets whether a warning should be rendered.</summary>
    public bool HasWarning => _warningText.Length > 0;

    /// <summary>Gets or sets the controller glyph family used by the overlay.</summary>
    public GlyphStyle GlyphStyle
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(GlyphStyle));
    } = GlyphStyle.Xbox;

    /// <summary>
    ///     Armed state of the destructive Close-Steam confirm. Lives here so
    ///     the bound title renders it — a direct Text write would fight the binding.
    /// </summary>
    public bool ConfirmingCloseLauncher
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(ConfirmingCloseLauncher)))
            {
                Raise(nameof(CloseLauncherText));
            }
        }
    }

    /// <summary>Gets the action label that switches between desktop and game mode.</summary>
    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";

    /// <summary>
    ///     Gets whether the desktop/game-mode row is offered at all. False for a
    ///     preview surface (Settings' "Test panel", <c>--overlay-test</c>), which has no
    ///     ShellSession, tray host or watchdog behind it — performing a real transition
    ///     there would exit Explorer and leave the user with no shell to recover to.
    /// </summary>
    public bool ModeSwitchAvailable { get; init; } = true;

    /// <summary>
    ///     Gets whether the Settings row is offered. False for a preview surface, which has no session
    ///     whose one Settings window it could open.
    /// </summary>
    public bool SettingsAvailable { get; init; } = true;

    /// <summary>Whether this surface may change real Windows idle timeouts.</summary>
    public bool PowerTimeoutsEditable { get; init; } = true;

    /// <summary>Gets the action label that starts or focuses the home application.</summary>
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";

    /// <summary>Gets the destructive-action label, including confirmation state.</summary>
    public string CloseLauncherText => ConfirmingCloseLauncher ? "Really?" : $"Close {HomeAppName}";

    /// <summary>
    ///     Whether the keep-awake row is shown at all (a session
    ///     <c>KeepAwakeService</c> exists; the Settings preview overlay has none).
    /// </summary>
    public bool ShowKeepAwake { get; init; }

    /// <summary>The user's manual wake mode (the row cycles it).</summary>
    public ManualWakeMode KeepAwakeManualMode
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(KeepAwakeManualMode)))
            {
                return;
            }

            Raise(nameof(KeepAwakeDescription));
        }
    } = ManualWakeMode.Off;

    /// <summary>Whether the automatic download keep-awake hold is active.</summary>
    public bool KeepAwakeDownloadActive
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(KeepAwakeDownloadActive)))
            {
                return;
            }

            Raise(nameof(KeepAwakeDescription));
        }
    }

    /// <summary>
    ///     System-wide wake-lock holder summary from the indicator poll
    ///     (for example "Standby blocked by steam.exe ×3"); empty when free, unknown,
    ///     or when only WSGM itself holds locks.
    /// </summary>
    public string WakeLockSummary
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(WakeLockSummary)))
            {
                Raise(nameof(KeepAwakeDescription));
            }
        }
    } = "";

    /// <summary>
    ///     Gets the status line rendered under the keep-awake row: WSGM's own
    ///     mode first, then other holders seen by the indicator, then the cycle hint.
    /// </summary>
    public string KeepAwakeDescription => KeepAwakeManualMode switch
    {
        ManualWakeMode.StandbyAndDisplay => "Standby blocked and screen kept on",
        ManualWakeMode.Standby => "Standby blocked until you turn this off",
        _ when KeepAwakeDownloadActive => "Held awake while Steam downloads",
        _ when WakeLockSummary.Length > 0 => WakeLockSummary,
        _ => "Off"
    };

    // Settable, not init-only: a config saved from another process while the panel
    // is open must be able to hide a feature the user just turned off, instead of
    // leaving a button that drives a now-disabled integration until the next reopen.

    /// <summary>
    ///     Whether the CEF library-tabs builder button is shown
    ///     (<c>Cef.Enabled &amp;&amp; Cef.LibraryTabs</c>). A hidden button removes the
    ///     only entry point to that CEF feature.
    /// </summary>
    public bool ShowLibraryTabs
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(ShowLibraryTabs)))
            {
                Raise(nameof(ShowSteamLibrarySection));
            }
        }
    } = true;

    /// <summary>
    ///     Whether the CEF SD-card library-manager button is shown
    ///     (<c>Cef.Enabled &amp;&amp; Cef.CardManager</c>).
    /// </summary>
    public bool ShowCardManager
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(ShowCardManager)))
            {
                return;
            }

            Raise(nameof(ShowSteamLibrarySection));
        }
    } = true;

    /// <summary>
    ///     Whether the CEF Format-SD-card and Add-library buttons are shown
    ///     (<c>Cef.Enabled &amp;&amp; Cef.SdFormat</c>).
    /// </summary>
    public bool ShowSdCard
    {
        get;
        set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(ShowSdCard)))
            {
                return;
            }

            Raise(nameof(ShowSteamLibrarySection));
        }
    } = true;

    /// <summary>
    ///     Whether the "STEAM LIBRARY" tools section has any visible button, so
    ///     its header is hidden rather than left orphaned when every CEF feature is off.
    /// </summary>
    public bool ShowSteamLibrarySection =>
        ShowLibraryTabs || ShowCardManager || ShowSdCard;

    /// <summary>
    ///     Whether Tools > Library Importer is offered: CEF is on and this session has a
    ///     library.
    /// </summary>
    public bool ShowGameLibrary
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowGameLibrary));
    }

    /// <summary>Whether Steam > Game Tools is offered: CEF is on and this session has the Game Tools owner.</summary>
    public bool ShowGameTools
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowGameTools));
    }

    /// <summary>Whether the session offers the Artwork tool.</summary>
    public bool ShowArtwork
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowArtwork));
    }

    /// <summary>Whether the Tools tab's Themes tile is offered: CEF is on and this session has the themes.</summary>
    public bool ShowThemes
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowThemes));
    }

    /// <summary>Whether the Tools tab's Animations tile is offered: CEF is on and this session has the animations.</summary>
    public bool ShowAnimations
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowAnimations));
    }

    /// <summary>Whether the session provides Steam UI sound customization.</summary>
    public bool ShowSounds
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(ShowSounds));
    }

    /// <summary>
    ///     Whether the launch-wrapper buttons configure the selected game in the
    ///     running Steam client (<c>Cef.Enabled</c>) instead of copying a command to the
    ///     clipboard for the user to paste by hand.
    /// </summary>
    public bool ConfigureLaunchOptionsLive
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(ConfigureLaunchOptionsLive)))
            {
                Raise(nameof(ShowRemoveLaunchWrapper));
            }
        }
    } = true;

    /// <summary>
    ///     Whether the "remove wrappers" row is shown — only meaningful when WSGM
    ///     can write the launch configuration itself.
    /// </summary>
    public bool ShowRemoveLaunchWrapper => ConfigureLaunchOptionsLive;

}
