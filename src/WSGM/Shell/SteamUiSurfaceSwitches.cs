using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>Every Steam UI surface switch, derived from the configuration in one place.</summary>
/// <param name="NativeQuickAccess">WSGM's rows in Steam's Quick Access menu.</param>
/// <param name="HostSurfaces">WSGM's own pages, menus and the plugin tab, which follow CEF itself.</param>
/// <param name="SurfaceObservation">Observing Steam's native surfaces independently of the custom rows.</param>
/// <param name="NetworkIndicator">The Big Picture header Wi-Fi indicator and the Internet page.</param>
/// <param name="DownloadSort">The download-queue sort buttons.</param>
/// <param name="LibraryBadge">The library badge on Steam's tiles.</param>
/// <param name="HomeCarousel">Home's connected-library carousel.</param>
/// <param name="CarouselShowUninstalled">Whether that carousel also lists owned games that are not installed.</param>
/// <param name="ScreensaverRows">WSGM's display-off rows in Steam's Screensaver settings.</param>
/// <param name="Glyphs">Whether WSGM presents handheld glyphs at all.</param>
/// <param name="GlyphProfile">The resolved plugin glyph profile, or null.</param>
/// <param name="NativeGlyphArtwork">Keeps Valve artwork while still hiding absent controls.</param>
internal sealed record SteamUiSurfaceSwitches(
    bool NativeQuickAccess,
    bool HostSurfaces,
    bool SurfaceObservation,
    bool NetworkIndicator,
    bool DownloadSort,
    bool LibraryBadge,
    bool HomeCarousel,
    bool CarouselShowUninstalled,
    bool ScreensaverRows,
    bool Glyphs,
    ImportedGlyphProfile? GlyphProfile,
    bool NativeGlyphArtwork)
{
    /// <summary>Every surface off, which is what a retraction leaves.</summary>
    internal static SteamUiSurfaceSwitches Off { get; } =
        new(false, false, false, false, false, false, false, false, false, false, null, false);

    /// <summary>Derives every switch from a configuration and the CEF master state.</summary>
    /// <param name="config">The configuration in force.</param>
    /// <param name="cefMasterEnabled">Whether the session's CEF master switch is on.</param>
    /// <param name="glyphProfile">The active plugin glyph profile, or null.</param>
    /// <param name="nativeArtwork">Whether Valve's own glyph artwork is kept.</param>
    /// <returns>The switches the host should hold.</returns>
    /// <remarks>
    ///     Every surface follows the master switch: off, nothing WSGM draws in Steam stays. Glyphs
    ///     also need device integration, because the profile comes from the device package.
    /// </remarks>
    internal static SteamUiSurfaceSwitches From(
        AppConfig config,
        bool cefMasterEnabled,
        ImportedGlyphProfile? glyphProfile,
        bool nativeArtwork)
    {
        var cef = config.Cef;
        var on = cefMasterEnabled && cef.Enabled;
        var glyphs = on && config.DeviceIntegration.Enabled;
        return new SteamUiSurfaceSwitches(
            on && cef.NativeQuickAccess,
            on,
            on,
            on && cef.WifiIndicator,
            on && cef.DownloadQueueSort,
            on && cef.CardManager,
            on && cef.ConnectedLibraryCarousel,
            cef.CarouselShowUninstalled,
            on,
            glyphs,
            glyphs ? glyphProfile : null,
            nativeArtwork);
    }
}
