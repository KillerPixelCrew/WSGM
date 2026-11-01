using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     Owns one surface’s theme browse state; disposing cancels its queries without disposing the shared theme
///     service. The owner’s commands go to <see cref="ISteamThemesBackend" /> directly.
/// </summary>
internal interface IThemeBrowseSession : IChangeSource, IDisposable
{
    /// <summary>Local tab selection; changing it directly does not publish a state notification.</summary>
    string Tab { get; set; }

    /// <summary>Reads the current theme projection without starting a query.</summary>
    /// <returns>Shared installed state combined with this session’s browse state.</returns>
    SteamThemesState ReadState();

    /// <summary>Requests cancellation of browse and detail queries without joining them or publishing a notification.</summary>
    void CancelQueries();

    /// <summary>Shows one of the page's tabs.</summary>
    /// <param name="tab">browse, installed, profiles or settings.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the active-tab state was updated.</returns>
    Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Asks the store for the first page matching a filter, order and search.</summary>
    /// <param name="filter">Store filter id; empty selects all themes.</param>
    /// <param name="order">Store sort id; empty selects the default order.</param>
    /// <param name="search">Search text; empty adds no text restriction.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the browse request; results and errors arrive through publications.</returns>
    Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search,
        CancellationToken cancellationToken);

    /// <summary>Asks the store for the next page of the current listing.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether another page was accepted, or a refusal while loading or after the final result.</returns>
    Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken);

    /// <summary>Opens one store listing's details.</summary>
    /// <param name="id">DeckThemes store theme id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the detail request; loading completes through published state.</returns>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the details.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after closing the detail selection.</returns>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);
}
