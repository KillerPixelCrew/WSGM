using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Browser;

namespace WSGM.Shell;

/// <summary>Trusted quick-lookup chrome. Valve's isolated native browser owns all arbitrary content.</summary>
internal static class SteamBrowserSurface
{
    internal const string PatchId = "wsgm.browser";
    internal const string Route = "/wsgm/browser";
    internal const string Template = "browser";
    internal const string GateName = "browser";

    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "wsgm-browser-v1:native-game-overlay", "Browser",
        [SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.ShowModal]);

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

internal sealed record SteamBrowserState(string Home, string SearchPrefix);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamBrowserState))]
internal partial class SteamBrowserJson : JsonSerializerContext;
