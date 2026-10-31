using System;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Browser;

namespace WSGM.Shell;

/// <summary>Trusted native-browser entry. Valve's isolated browser owns all arbitrary content.</summary>
internal static class SteamBrowserSurface
{
    internal const string PatchId = "wsgm.browser";
    internal const string Route = "/wsgm/browser";
    internal const string Template = "browser";
    internal const string GateName = "browser";

    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "wsgm-browser-v3:native-address-editor", "Browser");

    internal static ISteamUiModule Module(Func<bool> enabled)
    {
        return new SteamUiModule("browser", [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled,
                    () => new ValueTask<SteamBrowserState?>(new SteamBrowserState(BrowserNavigation.Home, BrowserNavigation.SearchPrefix)),
                    SteamBrowserJson.Default.SteamBrowserState)
            ]);
    }
}
