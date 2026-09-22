using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>
///     Steam's per-game gear menu: WSGM's own entries first, then whatever admitted packages
///     contribute.
/// </summary>
/// <remarks>
///     <para>
///         This is the menu the cog button on a game's page opens, and the placement Decky's
///         SteamGridDB plugin uses for the same job.
///     </para>
///     <para>
///         It is deliberately not conditional on a plugin source existing. While artwork was a
///         bundled package, the only thing that could put an item here was an admitted plugin — and
///         the bundled package never actually shipped, so the menu rendered with nothing in it and
///         the artwork browser had no way in at all. WSGM owns its own entries now, so the menu is
///         populated whether or not any package is installed.
///     </para>
///     <para>
///         WSGM's ids carry a reserved prefix. A package can neither answer for one nor displace it
///         by choosing the same id.
///     </para>
/// </remarks>
internal sealed class SteamGameContextMenuBackend : ISteamGameContextMenuBackend
{
    /// <summary>The id of WSGM's own artwork entry. Reserved: no package may use it.</summary>
    internal const string ArtworkId = "wsgm.change-artwork";

    /// <summary>The prefix every WSGM-owned entry carries.</summary>
    private const string ReservedPrefix = "wsgm.";

    private readonly CommonPluginSteamUiSource? _pluginSteamUi;
    private readonly Func<uint, CancellationToken, Task<SteamUiCommandResult>>? _openArtwork;
    private readonly Func<uint, string>? _artworkRoute;

    /// <summary>Creates the menu over WSGM's artwork browser and an optional plugin source.</summary>
    /// <param name="pluginSteamUi">The admitted-package projection, or null when there is none.</param>
    /// <param name="openArtwork">Opens the artwork page for a game, or null without one.</param>
    /// <param name="artworkRoute">The route that shows the artwork page for a game.</param>
    internal SteamGameContextMenuBackend(
        CommonPluginSteamUiSource? pluginSteamUi,
        Func<uint, CancellationToken, Task<SteamUiCommandResult>>? openArtwork,
        Func<uint, string>? artworkRoute)
    {
        _pluginSteamUi = pluginSteamUi;
        _openArtwork = openArtwork;
        _artworkRoute = artworkRoute;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ActivateAsync(
        uint appId, string id, CancellationToken cancellationToken)
    {
        if (id == ArtworkId)
        {
            if (_openArtwork is null || _artworkRoute is null)
            {
                return new SteamUiCommandResult(false, "Artwork is unavailable in this session.");
            }

            var opened = await _openArtwork(appId, cancellationToken).ConfigureAwait(false);
            if (!opened.Succeeded)
            {
                return opened;
            }

            // The route travels in the payload the gate reads, the same shape a plugin action's
            // answer takes, so the menu opens a page through one contract.
            return new SteamUiCommandResult(true, null, System.Text.Json.JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["route"] = _artworkRoute(appId) }));
        }

        // A reserved id this build does not answer is refused here rather than handed to a package,
        // which could otherwise claim an entry WSGM stopped offering.
        if (id.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            return new SteamUiCommandResult(false, "That menu entry is no longer available.");
        }

        return _pluginSteamUi is null
            ? new SteamUiCommandResult(false, "That menu entry is no longer available.")
            : await _pluginSteamUi.ActivateAsync(appId, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The entries Steam should currently render in a game's gear menu.</summary>
    internal SteamGameContextMenuState ReadState()
    {
        List<SteamGameContextMenuItem> items = [];
        if (_openArtwork is not null)
        {
            items.Add(new SteamGameContextMenuItem(ArtworkId, "Change Artwork…"));
        }

        if (_pluginSteamUi is not null)
        {
            items.AddRange(_pluginSteamUi.ReadGameContextMenu().Items
                .Where(item => !item.Id.StartsWith(ReservedPrefix, StringComparison.Ordinal)));
        }

        return new SteamGameContextMenuState(items);
    }
}
