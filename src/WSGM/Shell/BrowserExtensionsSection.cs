using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Opens trusted lookup controls in the window from which Quick Access was invoked.</summary>
internal sealed class BrowserExtensionsSection : IExtensionsTabSection
{
    public string SectionId => "wsgm.browser";

    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        return new SteamExtensionsTabItem(SectionId, "Browser", "", "Ready",
            "Look up a guide in Steam's browser for this game.",
            [new SteamExtensionsTabAction(SectionId + ".open", "Open Browser")]);
    }

    public Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(id == SectionId + ".open"
            ? SteamUiCommandResult.Route(SteamBrowserSurface.Route)
            : new SteamUiCommandResult(false, "That browser action is unavailable."));
    }

    public Task<SteamUiCommandResult> ConfigureExtensionAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SteamUiCommandResult(false, "Browser has no settings here."));
    }
}
