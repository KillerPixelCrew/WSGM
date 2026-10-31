using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

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
