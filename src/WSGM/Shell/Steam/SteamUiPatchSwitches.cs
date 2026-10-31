using System.Collections.Generic;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Derives every registered Steam UI patch's switch from the session's surface switches. Stateless:
///     the session host calls it under its switch gate whenever the switches or an input they read
///     move, and keeps the global switch itself.
/// </summary>
internal static class SteamUiPatchSwitches
{
    /// <summary>Sets every registered patch's switch from the given switches. The caller holds the switch gate.</summary>
    /// <param name="patches">The session's patch registry.</param>
    /// <param name="switches">The switches in force.</param>
    /// <param name="backends">The session's services; the theme cascade and the plugin rows read them.</param>
    /// <param name="pluginPatchIds">The registered plugin patches' ids.</param>
    /// <param name="overlayActivationId">The overlay-activation patch, switched by the host itself.</param>
    internal static void SetPatchStates(
        SteamUiPatchManager patches, SteamUiSurfaceSwitches switches, SteamUiBackends backends,
        IReadOnlySet<string> pluginPatchIds, string overlayActivationId)
    {
        // The registry is the source of truth for which patches exist; a hand-kept id list here
        // drifts. Glyphs and download sorting have independent switches; the network gate may also
        // outlive native QAM to keep the configured header indicator.
        var components = switches.NativeQuickAccess;
        var host = switches.HostSurfaces;
        foreach (var patch in patches.GetSnapshots())
        {
            if (patch.Id == SteamInputGlyphStylePatch.PatchId || patch.Id == overlayActivationId)
            {
                continue;
            }

            var enabled = patch.Id switch
            {
                SteamDownloadSort.PatchId => switches.DownloadSort,
                SteamLibraryBadgeSurface.PatchId or SteamLibraryBadgeSurface.DetailsPatchId
                    => LibraryAvailabilityWanted(switches),
                SteamLibraryBadgeSurface.AvailabilityPatchId => ManagedAvailabilityWanted(switches),
                SteamHomeCarouselSurface.PatchId => switches.HomeCarousel,
                SteamScreensaverSurface.PatchId => ScreensaverRowsWanted(switches, backends.DisplayTimeouts),
                SteamUiBridgePatch.PatchId => host,
                SteamNetworkSurface.PatchId => components || switches.NetworkIndicator,
                // Host-rendered surfaces follow CEF itself. Native Quick Access can be off while a
                // user still wants the artwork page and the plugin tab.
                SteamPageSurface.PatchId or SteamExtensionsTabSurface.PatchId
                    or SteamGameContextMenuSurface.PatchId or SteamPowerMenuSurface.PatchId
                    or SteamArtworkBrowserSurface.PatchId
                    or SteamLaunchActionsSurface.PatchId
                    or SteamCardManagerSurface.PatchId or SteamLibraryTabsSurface.PatchId
                    or SteamLibraryImportSurface.PatchId or SteamEmulatorSurface.PatchId
                    or SteamWsgmSettingsSurface.PatchId
                    or SteamNavigationPanelSurface.PatchId or SteamNativeSettingsSurface.PatchId
                    or SteamThemesSurface.PatchId
                    or SteamAnimationsSurface.PatchId or SteamSoundOverrideSurface.PatchId
                    or SteamSoundsSurface.PatchId or SteamCustomizationSurface.PatchId
                    or NotesSteamSurface.PatchId or SteamBrowserSurface.PatchId => host,
                // The cascade follows the themes' own switch as well; off, the gate is retracted and
                // every owned node leaves every window.
                SteamThemeStyleSurface.PatchId => host && backends.Themes is { Enabled: true },
                SteamSettingsQuickAccessRow.PatchId => components
                                                       || (host && backends.PluginSteamUi?.ReadModules().Count > 0),
                _ when pluginPatchIds.Contains(patch.Id) => host,
                _ => components
            };
            patches.SetPatchEnabled(patch.Id, enabled);
        }
    }

    /// <summary>
    ///     Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <param name="patches">The session's patch registry.</param>
    /// <param name="switches">The switches in force; the caller holds the switch gate.</param>
    /// <param name="glyphs">The glyph presentation the stylesheet is built from.</param>
    /// <returns>Whether the stylesheet is now switched on.</returns>
    /// <remarks>
    ///     One switch, because there is one stylesheet. The previous four independent tier switches
    ///     existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    ///     does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    internal static bool SetGlyphDeliveryPatchStates(
        SteamUiPatchManager patches, SteamUiSurfaceSwitches switches, SteamInputGlyphDeliveryState glyphs)
    {
        var presentation = glyphs.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        var deliver = switches.Glyphs
                      && presentation is not null
                      && (presentation.StableResources.Count > 0
                          || presentation.ControllerImages.Count > 0
                          || presentation.AbsentControls.Count > 0);

        // Three independent conditions, and failing any of them leaves the Steam Input page showing
        // Valve's Steam Deck artwork instead of the handheld's own. The patch then reports itself
        // Disabled, which is honest but says nothing about which condition was missing — the
        // setting, a profile that never resolved, or a profile that resolved with nothing to draw.
        Log.Change(
            "steam.ui.glyphs",
            $"Steam Input glyph delivery {(deliver ? "enabled" : "disabled")}: "
            + $"setting={switches.Glyphs}, profile={presentation is not null}, "
            + $"stableResources={presentation?.StableResources.Count ?? 0}, "
            + $"controllerImages={presentation?.ControllerImages.Count ?? 0}, "
            + $"absentControls={presentation?.AbsentControls.Count ?? 0}",
            deliver ? LogLevel.Info : LogLevel.Warn);
        patches.SetPatchEnabled(SteamInputGlyphStylePatch.PatchId, deliver);
        return deliver;
    }

    /// <summary>Whether badges or managed shortcuts require the shared library surface.</summary>
    /// <param name="switches">The switches in force.</param>
    /// <returns>True while the badge is on, or while the host surfaces are on and managed shortcuts exist.</returns>
    internal static bool LibraryAvailabilityWanted(SteamUiSurfaceSwitches switches)
    {
        return switches.LibraryBadge || (switches.HostSurfaces && LibraryBadges.HasManagedShortcuts);
    }

    /// <summary>Whether the Screensaver rows are wanted: they follow the host surfaces and need a timeout owner.</summary>
    /// <param name="switches">The switches in force.</param>
    /// <param name="timeouts">The session's display-off timeouts, or null without one.</param>
    /// <returns>True while the host surfaces are on and the session owns display timeouts.</returns>
    internal static bool ScreensaverRowsWanted(SteamUiSurfaceSwitches switches, DisplayTimeouts? timeouts)
    {
        return switches.HostSurfaces && timeouts is not null;
    }

    /// <summary>Whether the availability gate, which claims Steam's library filter, is needed.</summary>
    /// <remarks>Decorative badges alone keep the badge and details gates, never this one.</remarks>
    private static bool ManagedAvailabilityWanted(SteamUiSurfaceSwitches switches)
    {
        return LibraryBadges.HasManagedShortcuts && LibraryAvailabilityWanted(switches);
    }
}
