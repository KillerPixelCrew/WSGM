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

/// <summary>
///     Owns one surface’s animation selection and detail state over a borrowed shared library. The owner’s
///     commands go to <see cref="ISteamAnimationsBackend" /> directly.
/// </summary>
internal interface IAnimationBrowseSession : IChangeSource, IDisposable
{
    /// <summary>Local tab selection; changing it directly does not publish a state notification.</summary>
    string Tab { get; set; }

    /// <summary>Reads the current animation projection without starting a query.</summary>
    /// <returns>Shared library state combined with this session’s tab and detail selection.</returns>
    SteamAnimationsState ReadState();

    /// <summary>Shows one of the page's tabs.</summary>
    /// <param name="tab">browse, library or settings.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the tab state was updated.</returns>
    Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Lists what matches a sort and search, fetching the repository once.</summary>
    /// <param name="sort">Published sort id; unknown ids fall back to the default order.</param>
    /// <param name="search">Case-insensitive movie search text; empty shows all matching movies.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the browse state; repository loading completes through publications.</returns>
    Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken cancellationToken);

    /// <summary>Shows the next page of what matches.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the visible result window was advanced; no remaining items is a successful no-op.</returns>
    Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken cancellationToken);

    /// <summary>Opens one movie for a closer look.</summary>
    /// <param name="id">Id in the current repository listing or installed library.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the detail selection exists and was opened.</returns>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the closer look.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after clearing the detail selection.</returns>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one artwork browsing lifetime; dispose when the surface closes.</summary>
internal interface IArtworkBrowseSession : ISteamArtworkBrowserBackend, IChangeSource, IDisposable
{
    /// <summary>Reads the current artwork projection without starting a query.</summary>
    /// <returns>The opened game’s state, or null before a game has been opened.</returns>
    SteamArtworkBrowserState? ReadState();

    /// <summary>Opens one game and starts gathering its artwork candidates.</summary>
    /// <param name="id">Nonzero Steam AppID.</param>
    /// <param name="title">Title hint for shortcuts Steam has not listed yet, or null.</param>
    /// <param name="token">Cancels request admission; accepted browsing runs under the session lifetime.</param>
    /// <returns>Whether browsing was accepted; completion does not wait for candidate loading.</returns>
    Task<SteamUiCommandResult> OpenAsync(uint id, string? title, CancellationToken token);

    /// <summary>Queries Steam’s game list within the browsing lifetime.</summary>
    /// <returns>Available games or a result describing why the list could not be read.</returns>
    Task<SteamLibraryReadResult> ReadGamesAsync();

    /// <summary>Reads the opened game’s saved logo placement.</summary>
    /// <returns>The saved placement, or null when absent or unreadable; callers use default placement.</returns>
    Task<SteamLogoPosition?> ReadLogoPositionAsync();

    /// <summary>Cancels the current lookup generation and publishes a nonloading state; in-flight work is not joined.</summary>
    void CancelBrowsing();
}

/// <summary>Projects the shared library backend for overlay consumers; the session owns its lifetime.</summary>
internal interface IGameLibraryOverlaySource : IGameLibraryBackend, IChangeSource
{
    /// <summary>Reads the shared library’s latest published review.</summary>
    /// <returns>A snapshot for rendering; no scan or Steam query is started.</returns>
    GameLibraryState ReadState();

    /// <summary>Reads evidence already gathered for one review entry.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <returns>Entry details, or null when the entry is no longer listed.</returns>
    GameLibraryDetails? ReadDetails(string id);

    /// <summary>Reads cached candidates for one review entry’s artwork slot.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <returns>Candidates and loading status, or null when the entry or slot is unavailable.</returns>
    GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset);
}
