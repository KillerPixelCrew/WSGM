using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>One Quick Access Extensions entry of the library manager: a row with the action that opens its page.</summary>
/// <param name="service">The library manager whose page the entry opens.</param>
/// <param name="sectionId">The Extensions-tab item and page namespace.</param>
/// <param name="openId">The action that opens the page.</param>
/// <param name="route">The page's route.</param>
/// <param name="name">The row's title.</param>
/// <param name="openLabel">The open action's label.</param>
/// <param name="available">Whether the entry may be offered.</param>
/// <param name="detail">The row's detail line.</param>
internal sealed class LibraryManagerSection(
    LibraryManagerService service,
    string sectionId,
    string openId,
    string route,
    string name,
    string openLabel,
    Func<LibraryManagerService, bool> available,
    Func<LibraryManagerService, string> detail) : IExtensionsTabSection
{
    /// <inheritdoc />
    public string SectionId => sectionId;

    /// <inheritdoc />
    public SteamExtensionsTabItem? ReadExtensionsItem()
    {
        if (!available(service))
        {
            return null;
        }

        return new SteamExtensionsTabItem(
            sectionId,
            name,
            string.Empty,
            service.Busy ? "Working…" : "Ready",
            detail(service),
            [new SteamExtensionsTabAction(openId, openLabel)]);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != openId || !available(service))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer available."));
        }

        // The page shows what the last reading had and a fresh scan replaces it on arrival.
        service.RequestRefresh();
        return Task.FromResult(SteamUiCommandResult.Route(route));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."));
    }
}
