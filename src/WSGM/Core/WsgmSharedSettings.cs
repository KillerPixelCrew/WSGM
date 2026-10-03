using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>One configuration field edited by Settings and by another WSGM surface.</summary>
internal sealed record WsgmSharedSetting(string Name, Func<AppConfig, object> Read, Action<AppConfig, object> Write);

/// <summary>The shared fields whose fresh values an unedited Settings window must preserve.</summary>
internal static class WsgmSharedSettings
{
    internal static IReadOnlyList<WsgmSharedSetting> All { get; } =
    [
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
        new("SteamInputLeaseEnabled", config => config.SteamInputLeaseEnabled,
            (config, value) => config.SteamInputLeaseEnabled = (bool)value),
        new("SteamInputManagementEnabled", config => config.SteamInputManagementEnabled,
            (config, value) => config.SteamInputManagementEnabled = (bool)value),
        new("StartMode", config => config.StartMode, (config, value) => config.StartMode = (SessionStartMode)value),
        new("DeviceIntegration.Enabled", config => config.DeviceIntegration.Enabled,
            (config, value) => config.DeviceIntegration.Enabled = (bool)value),
        new("DeviceIntegration.AutoTdpEnabled", config => config.DeviceIntegration.AutoTdpEnabled,
            (config, value) => config.DeviceIntegration.AutoTdpEnabled = (bool)value),
        new("Profiles.Global.ControllerTarget",
            config => config.Profiles.Global.ControllerTarget ?? ManagedControllerTarget.SteamDeckComposite,
            (config, value) => config.Profiles.Global.ControllerTarget = (ManagedControllerTarget)value),
        new("DeviceIntegration.GlyphSelection", config => config.DeviceIntegration.GlyphSelection,
            (config, value) => config.DeviceIntegration.GlyphSelection = (DeviceGlyphSelection)value)
    ];

    internal static WsgmSharedSetting Get(string name)
    {
        return All.Single(field => field.Name == name);
    }
}
