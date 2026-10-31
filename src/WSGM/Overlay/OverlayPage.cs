namespace WSGM.Overlay;

/// <summary>Stable page identifiers used by the in-overlay navigation stack.</summary>
internal enum OverlayPage
{
    QuickAccess,
    Steam,

    /// <summary>The Steam library category page.</summary>
    SteamLibrary,

    /// <summary>The per-game launch fixes category page.</summary>
    SteamLaunchFixes,
    SteamLibraryTabs,
    SteamCardManager,

    /// <summary>The Game Library: bring other launchers' games into Steam.</summary>
    SteamGameLibrary,
    SteamLaunchConfiguration,
    SteamStorageFormat,
    Device,
    DeviceOverview,

    /// <summary>Graphics controls supplied by common GPU plugins, independent of the device package.</summary>
    DeviceGpu,
    DevicePowerAndThermals,
    DeviceControllerAndMotion,
    DeviceOem,
    DeviceLightingAndFeatures,
    DeviceColor,
    DeviceDiagnostics,

    /// <summary>One plugin-declared Device section; the route carries which one.</summary>
    DevicePluginSection,

    System,

    /// <summary>WSGM Settings, the Windows Task Manager and the UAC prompt policy.</summary>
    SystemTools,

    /// <summary>The frame limit, overlay and per-application profile rows, when Device is off.</summary>
    SystemPerformance,

    /// <summary>Removable storage actions.</summary>
    SystemStorage,

    /// <summary>Panel brightness and the display routes.</summary>
    SystemDisplay,

    /// <summary>Rows published by the enabled common plugins.</summary>
    SystemPlugins,

    /// <summary>The Steam themes: browse, install and manage CSS Loader themes.</summary>
    SystemThemes,
    SystemArtwork,

    /// <summary>The boot movie: browse, download and choose what Big Picture starts with.</summary>
    SystemAnimations,

    /// <summary>Steam UI sound packs and previews.</summary>
    SystemSounds,

    /// <summary>The on-screen keyboard and the Steam Input handoff.</summary>
    SystemController,

    /// <summary>Version, licence and the people and projects WSGM thanks.</summary>
    SystemAbout,
    Power,

    /// <summary>Keep Awake, the wake-lock list and the sign-in after standby.</summary>
    PowerWake,

    /// <summary>The display and standby idle timeouts.</summary>
    PowerTimeouts,

    /// <summary>Standby, hibernate, restart and shut down.</summary>
    PowerActions,

    /// <summary>
    ///     Steam start/focus, desktop, Big Picture and Steam exit. A root tab of its own until
    ///     2026-09-11: four buttons did not justify one, and they are lifecycle transitions, which
    ///     is what the rest of Power is.
    /// </summary>
    PowerSession,
    PowerWakeLocks,

    /// <summary>The standalone emulator downloader and updater under Tools.</summary>
    EmulatorManager,

    /// <summary>User-configured desktop programs and their shared launch-or-focus actions.</summary>
    Programs,

    /// <summary>Structured game notebooks, ink and screenshot captures.</summary>
    Notes,

    /// <summary>The session's browser and guide lookup.</summary>
    SystemBrowser,

    /// <summary>Local image and scene screensaver sources.</summary>
    Screensavers
}
