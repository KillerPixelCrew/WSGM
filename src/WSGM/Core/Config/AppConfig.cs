using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Persisted user settings and exact Windows-state snapshots for WSGM.</summary>
public sealed class AppConfig
{
    /// <summary>The stored shape this build reads and writes. 2.0 files carry no version and read as 0.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     The default WSGM accent (Handheld Companion orange). The single source for the accent digits;
    ///     <c>Themes/Palette.axaml</c> repeats them because XAML cannot read this constant.
    /// </summary>
    public const string DefaultAccentColor = "#FFFF9D3D";

    /// <summary>
    ///     Shape of the stored document. Reading migrates an older file in memory and a newer one loads best
    ///     effort; both hold <see cref="CurrentSchemaVersion" /> afterwards, so the next save writes it.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Explicit common-plugin preferences, independent of effective state publications.</summary>
    public List<CommonPluginConfiguration> PluginConfigurations { get; set; } = [];

    /// <summary>Explicit activation choices for installed common plugins. Empty means none are loaded.</summary>
    public List<CommonPluginInstanceConfig> PluginInstances { get; set; } = [];

    /// <summary>Explicit built-in GPU activation; null only until legacy preferences are migrated on load.</summary>
    public GpuDriverConfig? GpuDrivers { get; set; }

    /// <summary>Whether the user accepted the initial Steam CEF plugin trust warning.</summary>
    public bool SteamCefPluginWarningAccepted { get; set; }

    /// <summary>Optional device-plugin platform, ownership, and desired-state settings.</summary>
    public DeviceIntegrationConfig DeviceIntegration { get; set; } = new();

    /// <summary>Optional RTSS policy, independent from Device Integration.</summary>
    public PerformanceConfig Performance { get; set; } = new();

    /// <summary>The Global profile and every per-game profile. See <c>docs\profiles.md</c>.</summary>
    public ProfileConfig Profiles { get; set; } = new();

    /// <summary>Artwork provider credentials and the Steam browser's tab layout.</summary>
    public ArtworkConfig Artwork { get; set; } = new();

    /// <summary>How the Game Library treats titles nobody has decided anything about.</summary>
    public GameLibraryConfig GameLibrary { get; set; } = new();

    /// <summary>The Steam themes: whether they are installed into Steam, and what is WSGM's own about them.</summary>
    public ThemesConfig Themes { get; set; } = new();

    /// <summary>The boot movie Big Picture starts with, and the shuffle.</summary>
    public AnimationsConfig Animations { get; set; } = new();

    /// <summary>The active Audio Loader-compatible Steam UI sound pack.</summary>
    public SoundsConfig Sounds { get; set; } = new();

    /// <summary>Lazy Overlay browser memory and page zoom preferences; independent of Steam's browser.</summary>
    public BrowserOptions Browser { get; set; } = new();

    /// <summary>User-registered local screensaver image folders and scene packs.</summary>
    public ScreensaverConfig Screensavers { get; set; } = new();

    /// <summary>Per-user rumble strength and bounded-pulse perception calibration.</summary>
    public RumbleCalibrationConfig RumbleCalibration { get; set; } = new();

    /// <summary>
    ///     Restart Steam automatically when it exits. Steam itself is located
    ///     via the registry (see Core.Steam) — there is nothing else to configure.
    /// </summary>
    public bool SteamAutoRelaunch { get; set; }

    /// <summary>Whether the complete Steam client is launched at medium integrity.</summary>
    /// <remarks>
    ///     Off by default, which starts Steam at WSGM's own integrity. Elevated Steam is the deliberate
    ///     default because several WSGM mechanisms drive the running client and a mismatched pair loses
    ///     UIPI messages, but it also elevates every game Steam starts. This is the user-owned choice
    ///     between the two, and it applies to Steam itself rather than to individual games, which
    ///     <c>WSGM.Launch</c> de-elevates independently.
    /// </remarks>
    public bool SteamLaunchUnelevated { get; set; }

    /// <summary>
    ///     Keep a card's injected library tab after the card is
    ///     ejected. The games show as not-installed until it is reinserted.
    /// </summary>
    public bool KeepEjectedCardTabs { get; set; } = true;

    /// <summary>
    ///     Tracked removable Steam libraries, keyed by content id, used to
    ///     maintain per-card injected library tabs.
    /// </summary>
    public List<CardLibraryConfig> CardLibraries { get; set; } = [];

    /// <summary>
    ///     Cards forgotten while still inserted. Discovery skips these identities
    ///     until a scan observes them absent, so Forget does not immediately undo itself.
    /// </summary>
    public List<string> ForgottenInsertedCardIds { get; set; } = [];

    /// <summary>User-built custom filter tabs (the TabMaster analog).</summary>
    public List<CustomTabConfig> CustomTabs { get; set; } = [];

    /// <summary>
    ///     The library tab strip's display order as tab keys — Steam's native ids
    ///     (<c>AllGames</c>, <c>Collections</c>, …) and injected WSGM ids
    ///     (<c>wsgm-custom-…</c>, <c>wsgm-card-…</c>) mixed freely. Tabs not listed keep
    ///     their natural order after the listed ones; empty means Steam's default order
    ///     with WSGM tabs appended.
    /// </summary>
    public List<string> LibraryTabOrder { get; set; } = [];

    /// <summary>Native Steam tab ids the user removed from the library tab strip.</summary>
    public List<string> HiddenNativeTabs { get; set; } = [];

    /// <summary>
    ///     Native tabs as last observed in Steam's strip (id + localized title),
    ///     captured on every tab sync so the tab-order UI reflects the running Steam.
    /// </summary>
    public List<NativeTabConfig> KnownNativeTabs { get; set; } = [];

    /// <summary>
    ///     Games WSGM has pointed at the launch wrapper, with the launch
    ///     configuration each had beforehand so removing the wrapper can restore it.
    /// </summary>
    public List<LaunchWrapperConfig> LaunchWrappers { get; set; } = [];

    /// <summary>Programs to start before Steam, in launch order.</summary>
    public List<StartupAppConfig> StartupApps { get; set; } = [];

    /// <summary>
    ///     Delay before the FIRST startup app. Apps launch a few hundred ms
    ///     into the logon session, right after the game-mode display-scale change —
    ///     tools started into that window can hang (device-observed with Handheld
    ///     Companion, intermittent). This lets the session and the DPI change settle.
    /// </summary>
    public int StartupDelayMs { get; set; } = 3000;

    /// <summary>Delay between enabled startup-app launches, in milliseconds.</summary>
    public int StaggerDelayMs { get; set; } = 1500;

    /// <summary>Extra delay before Steam Big Picture is started at logon.</summary>
    public int SteamDelayMs { get; set; }

    /// <summary>
    ///     Mute system audio only while the screen is off and Steam reports an
    ///     active download (see Shell\DisplayOffMuteService). Screen-off alone stays
    ///     audible; download completion restores after a short grace period, and display
    ///     wake restores immediately. Only a mute WSGM applied itself is undone.
    /// </summary>
    public bool MuteWhileDisplayOff { get; set; }

    /// <summary>
    ///     Check GitHub once a day for a newer WSGM release (see Core\UpdateChecker). It only
    ///     notifies; the update runs when the user starts it from Settings.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    ///     Send the handheld back to sleep after a standby wake nothing accounts for (see
    ///     Shell\ModernStandbyGuard). Off by default: it decides on its own to suspend the machine, so
    ///     it is the user's to switch on. WSGM changes no Windows power settings and arms no wake
    ///     sources for it, so switching it off leaves nothing behind to restore.
    /// </summary>
    public bool ResuspendUnexplainedWakes { get; set; }

    /// <summary>Allow Steam's own storage pages to erase a drive through WSGM's format workflow.</summary>
    /// <remarks>
    ///     On by default, as an opt-out. It started life off by default on the argument that Steam's
    ///     page offers an erase to a single controller press; in use, Steam's Format Drive modal is
    ///     already a confirmation with a typed name, and <see cref="Shell.SdFormatManager" /> re-verifies
    ///     the disk's identity before every destructive step regardless of who asked. What the extra
    ///     gate actually did was make the feature look broken to anyone who had not found the toggle
    ///     (Claw, 2026-09-11). Off refuses only the erase; eject and library registration from that
    ///     page keep working.
    /// </remarks>
    public bool SteamStorageFormatEnabled { get; set; } = true;

    /// <summary>
    ///     Fullscreen "Please wait" cover at logon that hides startup-app
    ///     window flashes until Steam Big Picture is on screen (see Shell\BootSplash).
    /// </summary>
    public bool BootSplashEnabled { get; set; } = true;

    /// <summary>
    ///     Boot-splash appearance (see <see cref="SplashConfig" />); <see cref="BootSplashEnabled" /> controls
    ///     whether the splash runs at all. A fresh install starts on the shipped 2.0 preset; a configuration
    ///     that already carries a splash section keeps whatever it says, so an existing user's chosen look
    ///     survives the upgrade.
    /// </summary>
    public SplashConfig Splash { get; set; } = SplashPresets.Wsgm20();

    /// <summary>
    ///     UI accent color as an <c>#AARRGGBB</c>/<c>#RRGGBB</c> string, applied
    ///     to the Fluent theme and the Hc accent tokens at startup and on save.
    /// </summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    /// <summary>Gaussian blur of the live Overlay backdrop in physical pixels (0 to 60).</summary>
    public double OverlayBlurRadius { get; set; } = 8;

    /// <summary>
    ///     Whether the logon service starts WSGM at sign-in. Projected into boot.json
    ///     (see Core\BootManifest) because the SYSTEM service never parses this file. False leaves
    ///     the sign-in alone entirely; <see cref="StartMode" /> decides what a start becomes.
    /// </summary>
    public bool StartAtSignIn { get; set; } = true;

    /// <summary>
    ///     Which session mode a start produces. Independent of <see cref="StartAtSignIn" />,
    ///     because starting with Windows and taking the screen over are separate choices: a desktop
    ///     PC wants the first without the second.
    /// </summary>
    public SessionStartMode StartMode { get; set; } = SessionStartMode.Game;

    /// <summary>
    ///     Whether the user allowed WSGM to own how Steam starts. Until they do, WSGM changes
    ///     no startup entry; afterwards every start re-checks for new ones.
    /// </summary>
    public bool SteamAutostartTakeoverAccepted { get; set; }

    /// <summary>
    ///     The Steam startup entries WSGM turned off, with their previous state. An
    ///     install-lifecycle recovery snapshot: the uninstaller restores from it.
    /// </summary>
    public List<SteamAutostartRecord> SteamAutostartDisabled { get; set; } = [];

    /// <summary>
    ///     Whether the user chose in setup to turn off other handheld managers. Kept apart from the records
    ///     so the choice survives a run where nothing was found to turn off.
    /// </summary>
    public bool OtherManagersTakeoverAccepted { get; set; }

    /// <summary>
    ///     Services and tasks of other handheld managers (Handheld Companion, the makers' apps) that setup's
    ///     Full mode turned off, so the uninstaller can put back exactly those (see Core\OtherManagers).
    /// </summary>
    public List<OtherManagerRecord> OtherManagersDisabled { get; set; } = [];

    /// <summary>
    ///     Settle delay after explorer's shell window and taskbar both exist,
    ///     before the boot takeover cleanly shuts explorer down. Covers the logon prep
    ///     (Run keys, Startup folder, session services) that must complete once per
    ///     sign-in for touch features to survive game mode.
    /// </summary>
    public int ExplorerLogonSettleMs { get; set; } = 5000;

    /// <summary>
    ///     Whether WSGM manages the Steam Input lease around its focused
    ///     surfaces (overlay/taskbar). Off = the lease is never acquired: Steam Input's
    ///     desktop profile may take the controller while a WSGM panel is open, but
    ///     nothing is ever injected into Steam.
    /// </summary>
    /// <remarks>
    ///     Read when a surface opens, so turning it off takes effect at the NEXT surface
    ///     open — no restart, but not mid-surface either. A lease already applied for a
    ///     surface that is still on screen is deliberately never released early: the
    ///     release hands the controller back to Steam's desktop profile, which swallows
    ///     it from SDL system-wide, so the user who just turned this off from an open
    ///     Settings window would lose controller navigation on that very click. The lease
    ///     is scoped to the surface lifetime by specification — see docs\steam-input.md
    ///     and src\WSGM\Overlay\AGENTS.md.
    /// </remarks>
    public bool SteamInputLeaseEnabled { get; set; } = true;

    /// <summary>
    ///     Whether WSGM deploys its Steam Input shim into Steam's own install
    ///     directory as a search-order proxy DLL, so Steam loads it itself and WSGM
    ///     never writes into the Steam process.
    /// </summary>
    /// <remarks>
    ///     Off parks the deployed file beside itself instead of deleting it, and every
    ///     lease WSGM takes for its own surfaces fails open. <see cref="SteamInputLeaseEnabled" />
    ///     still decides whether the block is asked for at all; this decides how it gets
    ///     into Steam. Absent from an older config.json it defaults on, which is what
    ///     carries an upgrading device across without losing controller navigation.
    /// </remarks>
    public bool SteamInputManagementEnabled { get; set; } = true;

    /// <summary>How much detail reaches <c>wsgm.log</c>.</summary>
    /// <remarks>
    ///     Absent from an older config.json this defaults to <see cref="Core.LogVerbosity.Normal" />,
    ///     which records every transition, warning and failure. Verbose adds the debug level and is
    ///     meant for reproducing a specific problem, not for running on.
    /// </remarks>
    public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

    /// <summary>Whether AutoTDP writes a CSV trace of every control window.</summary>
    /// <remarks>
    ///     A diagnostic for reproducing an AutoTDP problem. Each control session gets its own file under
    ///     <c>autotdp-traces</c> beside <c>wsgm.log</c>. Recording never changes a control decision.
    /// </remarks>
    public bool AutoTdpTraceEnabled { get; set; }

    /// <summary>
    ///     Steam CEF integration master switch and per-feature sub-toggles
    ///     (see <see cref="CefConfig" />).
    /// </summary>
    public CefConfig Cef { get; set; } = new();

    /// <summary>Keyboard shortcut configuration for opening the overlay.</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>Controller shortcut configuration for opening the overlay.</summary>
    public GamepadChordConfig GamepadChord { get; set; } = new();

    /// <summary>Touch-edge gesture configuration for opening the overlay.</summary>
    public GestureConfig Gestures { get; set; } = new();

    /// <summary>
    ///     Rows pinned to the quick access sheet's home tab, as stable row ids
    ///     (a row's <c>Tag</c>: <c>home.steam</c>, <c>system.keep-awake</c>, a device
    ///     capability key, …) in display order. An id the running build cannot resolve
    ///     is kept (a device plugin's row survives the device being unplugged) but not
    ///     rendered. A new configuration starts with the Game Mode / Desktop switch pinned
    ///     (<c>home.desktop</c>, labelled "Return to Desktop" or "Back to Game Mode").
    /// </summary>
    public List<string> QuickAccessPins { get; set; } = ["home.desktop"];

    /// <summary>User-approved Programs entries; their pins use program:{stable id} in QuickAccessPins.</summary>
    public List<ProgramDefinition> Programs { get; set; } = [];

    /// <summary>Explicit source sizes added to a physical display's driver list.</summary>
    public List<ScaledResolutionAddition> ScaledResolutions { get; set; } = [];

    /// <summary>Original driver source lists captured before explicit edits.</summary>
    public List<ScaledResolutionBackup> ScaledResolutionBackups { get; set; } = [];

    /// <summary>Common plugin widget pins in display order; unavailable identities are retained.</summary>
    public List<PluginWidgetPin> PluginWidgetPins { get; set; } = [];

    /// <summary>
    ///     Last Windows power scheme explicitly selected and verified through the overlay.
    ///     This is a reference only; startup and config reload must not reapply it.
    /// </summary>
    public Guid? LastSelectedPowerSchemeId { get; set; }

    /// <summary>Controller glyph family displayed by the UI.</summary>
    public GlyphStyle GlyphStyle { get; set; } = GlyphStyle.Xbox;

    /// <summary>
    ///     Per-display scaling captured before game mode forced 100%. Non-empty
    ///     means "not yet restored" — survives crashes so recovery paths can put
    ///     scaling back, matched per display via the GDI source device name.
    /// </summary>
    public List<DisplayScaleEntry> SavedDisplayScaleEntries { get; set; } = [];

    /// <summary>What Game Mode entry and leave do to displays, and the automation around them.</summary>
    public GameModeLaunchConfiguration GameModeLaunch { get; set; } = new();

    /// <summary>
    ///     Runtime-owned record of a Game Mode session that has not returned yet. Settings
    ///     never writes this, so an editor open across a crash cannot discard the layout a recovery
    ///     start has to restore.
    /// </summary>
    public GameModeLaunchRecovery GameModeLaunchRecovery { get; set; } = new();

    /// <summary>
    ///     The Winlogon Shell snapshot that existed before WSGM installed itself.
    ///     Presence is separate from the string so an empty value remains distinguishable
    ///     from an absent value; kind preserves REG_EXPAND_SZ as well as REG_SZ.
    /// </summary>
    public string? PreviousShellValue { get; set; }

    /// <summary>Whether the original Winlogon Shell value has been captured.</summary>
    public bool PreviousShellSnapshotCaptured { get; set; }

    /// <summary>Whether the original Winlogon Shell value existed.</summary>
    public bool PreviousShellValueExists { get; set; }

    /// <summary>Registry type of the original Winlogon Shell value.</summary>
    public RegistryValueKind PreviousShellValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>
    ///     Snapshot of GamingConfiguration\StartupToGamingHome, which is changed
    ///     while WSGM is installed to keep Xbox Full Screen Experience from competing
    ///     for the session.
    /// </summary>
    public int PreviousStartupToGamingHomeValue { get; set; }

    /// <summary>Whether the original GamingConfiguration value has been captured.</summary>
    public bool PreviousStartupToGamingHomeSnapshotCaptured { get; set; }

    /// <summary>Whether the original GamingConfiguration value existed.</summary>
    public bool PreviousStartupToGamingHomeValueExists { get; set; }

    /// <summary>Registry type of the original GamingConfiguration value.</summary>
    public RegistryValueKind PreviousStartupToGamingHomeValueKind { get; set; } = RegistryValueKind.DWord;

    /// <summary>
    ///     UAC prompt-level values as they were before WSGM lowered them,
    ///     so the change can be undone exactly.
    /// </summary>
    public bool PreviousUacSnapshotCaptured { get; set; }

    /// <summary>Original administrator-consent prompt level.</summary>
    public int PreviousUacConsentPrompt { get; set; } = 5;

    /// <summary>Original secure-desktop prompt setting.</summary>
    public int PreviousUacSecureDesktop { get; set; } = 1;

    /// <summary>
    ///     Whether the lock-on-wake state was captured before WSGM changed it
    ///     (the exact per-scheme values live in the fields below).
    /// </summary>
    public bool PreviousLockOnWakeSnapshotCaptured { get; set; }

    /// <summary>Previous HKLM Personalization\NoLockScreen value (-1 = absent).</summary>
    public int PreviousNoLockScreen { get; set; } = -1;

    /// <summary>
    ///     Per-power-scheme CONSOLELOCK values (AC and DC) as they were before
    ///     WSGM flattened them to 0, so restore is exact even for mixed setups.
    /// </summary>
    public List<PowerSchemeConsoleLock> PreviousConsoleLockSchemeValues { get; set; } = [];

    /// <summary>
    ///     True when the CONSOLELOCK policy key already existed before WSGM;
    ///     false means WSGM created it and restore deletes the whole key.
    /// </summary>
    public bool PreviousConsoleLockPolicyKeyExisted { get; set; }

    /// <summary>Pre-existing CONSOLELOCK policy values (-1 = value absent).</summary>
    public int PreviousConsoleLockPolicyAc { get; set; } = -1;

    /// <summary>Pre-existing DC CONSOLELOCK policy value; <c>-1</c> means absent.</summary>
    public int PreviousConsoleLockPolicyDc { get; set; } = -1;
}
