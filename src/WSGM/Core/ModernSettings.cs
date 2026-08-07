using System;

namespace WSGM.Core;

/// <summary>Windows Settings pages that Quick Access can open directly.</summary>
internal enum ModernSettingsPage
{
    Bluetooth,
    Wifi,
}

/// <summary>Launches pages in the modern Windows Settings app without requiring Explorer.</summary>
internal static class ModernSettings
{
    /// <summary>Gets the registered Windows URI for a Settings page.</summary>
    /// <param name="page">The page to open.</param>
    /// <returns>The <c>ms-settings:</c> URI Windows uses to activate the page.</returns>
    internal static string UriFor(ModernSettingsPage page) => page switch
    {
        ModernSettingsPage.Bluetooth => "ms-settings:bluetooth",
        ModernSettingsPage.Wifi => "ms-settings:network-wifi",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };

    /// <summary>Opens a page through its registered Windows Settings URI.</summary>
    /// <param name="page">The page to open.</param>
    /// <returns>The result of the URI activation.</returns>
    internal static AppLauncher.LaunchResult Open(ModernSettingsPage page)
        => AppLauncher.StartProtocol(UriFor(page));
}
