using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Answers artwork operations shared by Steam pages and overlay browse sessions.</summary>
/// <remarks>Accepted background reads and writes use the owner lifetime; observe published state for their final outcome.</remarks>
internal interface ISteamArtworkBrowserBackend
{
    /// <summary>Selects and loads one published tab.</summary>
    /// <param name="tab">Id of a currently configured artwork or management tab.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the tab change was accepted; provider loading continues through published state.</returns>
    Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Applies one currently published opaque result.</summary>
    /// <param name="id">Opaque id from the current Assets publication.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the write was accepted; its final result is published in the page state.</returns>
    Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies one currently published official Steam asset.</summary>
    /// <param name="id">Opaque id from the current OfficialAssets publication.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the official-artwork write was accepted; its final result is published.</returns>
    Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken);

    /// <summary>Clears custom artwork from one published slot.</summary>
    /// <param name="tab">Published artwork-slot id whose custom image should be removed.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the clear operation was accepted, or why the slot could not be changed.</returns>
    Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Loads another result page when the provider reports one.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether another result load was accepted; rejects while loading or when no further page is available.</returns>
    Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken);

    /// <summary>Applies an image file the user chose with Steam's file picker.</summary>
    /// <param name="tab">The slot.</param>
    /// <param name="path">The file's full local path.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the apply was started, or why not.</returns>
    Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path, CancellationToken cancellationToken);

    /// <summary>Applies a transparent image to one slot, so the slot shows nothing.</summary>
    /// <param name="tab">The slot; every slot but the icon.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the apply was started, or why not.</returns>
    Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Replaces the active filter and reloads the current tab.</summary>
    /// <param name="filter">Replacement filters for the active result tab; at least one of Static or Animated must be enabled.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the reload was accepted; old result identities are invalidated.</returns>
    Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken);

    /// <summary>Searches configured providers for a manual game override.</summary>
    /// <param name="term">Nonblank manual game-search text.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the provider search; matches and errors arrive in published state.</returns>
    Task<SteamUiCommandResult> SearchGamesAsync(string term, CancellationToken cancellationToken);

    /// <summary>Selects an opaque game match, or clears it when the id is null.</summary>
    /// <param name="id">Opaque current GameMatches identity, or null to restore lookup by Steam app id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the match change and result reload were accepted.</returns>
    Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken);

    /// <summary>Saves a custom Steam logo position.</summary>
    /// <param name="anchor">Steam anchor such as TopLeft, CenterCenter or BottomRight.</param>
    /// <param name="width">Logo width percentage, from 5 through 100.</param>
    /// <param name="height">Logo height percentage, from 5 through 100.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the Steam write was accepted; its final result is published.</returns>
    Task<SteamUiCommandResult> SaveLogoPositionAsync(
        string anchor, int width, int height, CancellationToken cancellationToken);

    /// <summary>Clears Steam's custom logo position.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether restoring Steam's default logo position was accepted.</returns>
    Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken);
}
