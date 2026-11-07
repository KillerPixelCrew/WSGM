using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>The shared fields whose fresh values an unedited Settings window must preserve.</summary>
internal static class WsgmSharedSettings
{
    /// <summary>Gets the shared fields whose concurrent external edits participate in Settings merge.</summary>
    internal static IReadOnlyList<WsgmSharedSetting> All { get; } =
    [
        new("Browser.MemoryBehavior", config => config.Browser.MemoryBehavior,
            (config, value) => config.Browser.MemoryBehavior = (BrowserMemoryBehavior)value),
        new("Cef.Enabled", config => config.Cef.Enabled, (config, value) => config.Cef.Enabled = (bool)value),
        new("Cef.LibraryTabs", config => config.Cef.LibraryTabs,
            (config, value) => config.Cef.LibraryTabs = (bool)value),
        new("Cef.CardManager", config => config.Cef.CardManager,
            (config, value) => config.Cef.CardManager = (bool)value),
        new("Cef.SdFormat", config => config.Cef.SdFormat, (config, value) => config.Cef.SdFormat = (bool)value),
        new("Cef.ConnectedLibraryCarousel", config => config.Cef.ConnectedLibraryCarousel,
            (config, value) => config.Cef.ConnectedLibraryCarousel = (bool)value),
        new("Cef.CarouselShowUninstalled", config => config.Cef.CarouselShowUninstalled,
            (config, value) => config.Cef.CarouselShowUninstalled = (bool)value),
        new("Cef.WifiIndicator", config => config.Cef.WifiIndicator,
            (config, value) => config.Cef.WifiIndicator = (bool)value),
        new("Cef.NativeQuickAccess", config => config.Cef.NativeQuickAccess,
            (config, value) => config.Cef.NativeQuickAccess = (bool)value),
        new("Cef.DownloadKeepAwake", config => config.Cef.DownloadKeepAwake,
            (config, value) => config.Cef.DownloadKeepAwake = (bool)value),
        new("Cef.DownloadQueueSort", config => config.Cef.DownloadQueueSort,
            (config, value) => config.Cef.DownloadQueueSort = (bool)value),
        new("SteamStorageFormatEnabled", config => config.SteamStorageFormatEnabled,
            (config, value) => config.SteamStorageFormatEnabled = (bool)value),
        new("StartAtSignIn", config => config.StartAtSignIn, (config, value) => config.StartAtSignIn = (bool)value),
        new("MuteWhileDisplayOff", config => config.MuteWhileDisplayOff,
            (config, value) => config.MuteWhileDisplayOff = (bool)value),
        new("CheckForUpdates", config => config.CheckForUpdates,
            (config, value) => config.CheckForUpdates = (bool)value),
        new("ResuspendUnexplainedWakes", config => config.ResuspendUnexplainedWakes,
            (config, value) => config.ResuspendUnexplainedWakes = (bool)value),
        new("AutoTdpTraceEnabled", config => config.AutoTdpTraceEnabled,
            (config, value) => config.AutoTdpTraceEnabled = (bool)value),
        new("AccentColor", config => config.AccentColor, (config, value) => config.AccentColor = (string)value),
        new("OverlayBlurRadius", config => config.OverlayBlurRadius,
            (config, value) => config.OverlayBlurRadius = (double)value),
        new("StartupDelayMs", config => config.StartupDelayMs, (config, value) => config.StartupDelayMs = (int)value),
        new("StaggerDelayMs", config => config.StaggerDelayMs, (config, value) => config.StaggerDelayMs = (int)value),
        new("BootSplashEnabled", config => config.BootSplashEnabled,
            (config, value) => config.BootSplashEnabled = (bool)value),
        new("Performance.Enabled", config => config.Performance.Enabled,
            (config, value) => config.Performance.Enabled = (bool)value),
        new("Gestures.TopEdge", config => config.Gestures.TopEdge,
            (config, value) => config.Gestures.TopEdge = (bool)value),
        new("Gestures.BottomEdgeTaskbar", config => config.Gestures.BottomEdgeTaskbar,
            (config, value) => config.Gestures.BottomEdgeTaskbar = (bool)value),
        new("Gestures.LeftEdgeSteamMenu", config => config.Gestures.LeftEdgeSteamMenu,
            (config, value) => config.Gestures.LeftEdgeSteamMenu = (bool)value),
        new("Gestures.RightEdgeSteamQuickAccess", config => config.Gestures.RightEdgeSteamQuickAccess,
            (config, value) => config.Gestures.RightEdgeSteamQuickAccess = (bool)value),
        new("DeviceIntegration.ControllerManagementEnabled",
            config => config.DeviceIntegration.ControllerManagementEnabled,
            (config, value) => config.DeviceIntegration.ControllerManagementEnabled = (bool)value),
        new("SteamInputLeaseEnabled", config => config.SteamInputLeaseEnabled,
            (config, value) => config.SteamInputLeaseEnabled = (bool)value),
        new("SteamInputManagementEnabled", config => config.SteamInputManagementEnabled,
            (config, value) => config.SteamInputManagementEnabled = (bool)value),
        new("StartMode", config => config.StartMode, (config, value) => config.StartMode = (SessionStartMode)value),
        new("DeviceIntegration.Enabled", config => config.DeviceIntegration.Enabled,
            (config, value) => config.DeviceIntegration.Enabled = (bool)value),
        new("DeviceIntegration.AutoTdpEnabled", config => config.DeviceIntegration.AutoTdpEnabled,
            (config, value) => config.DeviceIntegration.AutoTdpEnabled = (bool)value),
        new("DeviceIntegration.PowerButtonShortPress", config => config.DeviceIntegration.PowerButtonShortPress,
            (config, value) => config.DeviceIntegration.PowerButtonShortPress = (PowerButtonShortPress)value),
        new("Profiles.Global.ControllerTarget",
            config => config.Profiles.Global.ControllerTarget ?? ProfileFields.DefaultControllerTarget,
            (config, value) => config.Profiles.Global.ControllerTarget = (ManagedControllerTarget)value),
        new("DeviceIntegration.GlyphSelection", config => config.DeviceIntegration.GlyphSelection,
            (config, value) => config.DeviceIntegration.GlyphSelection = (DeviceGlyphSelection)value),
        new("Artwork.SteamGridDbApiKey", config => config.Artwork.SteamGridDbApiKey,
            (config, value) => config.Artwork.SteamGridDbApiKey = (string)value),
        new("Artwork.ScreenscraperEnabled", config => config.Artwork.ScreenscraperEnabled,
            (config, value) => config.Artwork.ScreenscraperEnabled = (bool)value)
    ];

    /// <summary>Finds the shared-field accessor for an exact configuration key.</summary>
    /// <param name="name">Case-sensitive dotted key from <see cref="All" />.</param>
    /// <returns>The unique accessor for that key.</returns>
    /// <exception cref="InvalidOperationException">The key is absent or duplicated in the registry.</exception>
    internal static WsgmSharedSetting Get(string name)
    {
        return All.Single(field => field.Name == name);
    }
}
