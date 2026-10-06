using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The session-owned services behind WSGM's Steam UI surfaces.</summary>
/// <remarks>
///     The session builds every one of these, including the Windows power-policy readers and the fold
///     store, and disposes them after the Steam UI host. The host only reads them and wraps the device
///     coordinator for Steam's rows. A surface whose backend is null is not declared at all, so Steam
///     shows its own page there instead of a control with nothing behind it.
/// </remarks>
internal sealed record SteamUiBackends
{
    /// <summary>The RTSS-backed performance service.</summary>
    internal required PerformanceService Performance { get; init; }

    /// <summary>The profile owner Steam's per-game toggle and reset write to.</summary>
    internal required ProfileService Profiles { get; init; }

    /// <summary>The panel backlight, shared with the overlay.</summary>
    internal required NativeQamBrightnessService Brightness { get; init; }

    /// <summary>Which Quick Access sections the user opened.</summary>
    internal required QuickAccessFolds Folds { get; init; }

    /// <summary>The Windows power plans behind Steam's power-profile row.</summary>
    internal required NativeQamPowerProfileService PowerProfiles { get; init; }

    /// <summary>The hybrid-core preference behind its row.</summary>
    internal required NativeQamHybridCoreService HybridCores { get; init; }

    /// <summary>The device platform, or null when device integration is off.</summary>
    internal DeviceCoordinator? DeviceCoordinator { get; init; }

    /// <summary>The session's AutoTDP service, or null when it is not running.</summary>
    internal AutoTdpService? AutoTdp { get; init; }

    /// <summary>
    ///     What the device can back, for the reactivated performance panel. Null hides every
    ///     performance control.
    /// </summary>
    internal Func<NativeQamPerfSupport>? PerfSupport { get; init; }

    /// <summary>Applies a manually chosen refresh rate, or null.</summary>
    internal Func<int, bool>? ApplyRefreshRate { get; init; }

    /// <summary>Applies the variable refresh flag, or null when nothing can publish it.</summary>
    internal Func<bool, CancellationToken, Task<bool>>? ApplyVariableRefreshRate { get; init; }

    /// <summary>The session's audio manager, or null.</summary>
    internal AudioManager? Audio { get; init; }

    /// <summary>The live advanced-audio service, or null.</summary>
    internal AudioProfileService? AudioProfiles { get; init; }

    /// <summary>The session's radio manager, borrowed, or null.</summary>
    internal RadioManager? Radios { get; init; }

    /// <summary>Opens the session's Bluetooth prompt and status surface, or null.</summary>
    internal Func<bool>? ShowBluetoothPanel { get; init; }

    /// <summary>The display-resolution backend, or null when this session must not move the display.</summary>
    internal DisplayResolutionService? Resolution { get; init; }

    /// <summary>The bridge over the session's own storage managers, or null.</summary>
    internal SteamStorageBridge? Storage { get; init; }

    /// <summary>The display-off timeouts shared with the overlay, or null.</summary>
    internal DisplayTimeouts? DisplayTimeouts { get; init; }

    /// <summary>The common-plugin projection rendered through host-owned Steam surfaces, or null.</summary>
    internal CommonPluginSteamUiSource? PluginSteamUi { get; init; }

    /// <summary>The artwork browser behind Steam's Change Artwork page, or null.</summary>
    internal SteamArtworkBrowserSource? Artwork { get; init; }

    /// <summary>The Game Library behind Steam's import page, or null.</summary>
    internal GameLibraryService? LibraryImport { get; init; }

    /// <summary>WSGM's settings behind its page in Steam and its row in Steam's main menu, or null.</summary>
    internal WsgmSteamSettingsService? WsgmSettings { get; init; }

    /// <summary>The per-game processor boost mode behind Steam's Performance dropdown, or null.</summary>
    internal NativeQamCpuBoostService? CpuBoost { get; init; }

    /// <summary>The guide-chord mirror the editor's reset restores Valve's template through, or null.</summary>
    internal SteamGuideChordMirror? ChordMirror { get; init; }

    /// <summary>Steam's Switch to Desktop in the Big Picture power menu, or null.</summary>
    internal SteamPowerMenuBackend? PowerMenu { get; init; }

    /// <summary>The Steam themes behind their page, their Quick Access section and the cascade, or null.</summary>
    internal ThemeService? Themes { get; init; }

    /// <summary>The boot movie behind its page and its Quick Access section, or null.</summary>
    internal AnimationService? Animations { get; init; }

    /// <summary>The graphics capabilities shared by Quick Access and Steam Display, or null.</summary>
    internal SteamGraphicsService? Graphics { get; init; }

    /// <summary>Sound-pack assets published through the shared playback override gate, or null.</summary>
    internal SoundPackService? Sounds { get; init; }
}
