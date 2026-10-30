using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     The SD Card Manager state and commands the Overlay card manager uses; Steam's SD Card Manager page drives the
///     same owner, <see cref="LibraryManagerService" />.
/// </summary>
/// <remarks>
///     Reading state is cheap and does no I/O. A scan, a change made on either surface and a card insertion are
///     announced through <see cref="IChangeSource.Changed" /> and read back with <see cref="ReadCardState" />.
/// </remarks>
internal interface ICardManagerSource : IChangeSource
{
    /// <summary>The card manager as it is now.</summary>
    /// <returns>The state; cheap, no I/O.</returns>
    SteamCardManagerState ReadCardState();

    /// <summary>Scans the drives and rereads the configuration; concurrent requests share one run.</summary>
    /// <param name="cancellationToken">Cancels waiting for the run, not the run itself.</param>
    /// <returns>Completion of a refresh that started at or after this call.</returns>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the error and the notice.</summary>
    /// <param name="cancellationToken">Unused; the change is immediate.</param>
    /// <returns>Applied.</returns>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames a card everywhere it has a name, through the manager's one rename path.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>
    ///     Failure with the manager's reason when nothing was renamed; success otherwise, with any
    ///     partial-write note published as the notice.
    /// </returns>
    Task<SteamUiCommandResult> RenameCardAsync(string contentId, string name,
        CancellationToken cancellationToken = default);

    /// <summary>Keeps or drops the card's Steam tab.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="enabled">Whether to maintain a tab.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the preference was saved.</returns>
    Task<SteamUiCommandResult> SetCardEnabledAsync(string contentId, bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Hides a card from tab creation, or shows it again.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the preference was saved.</returns>
    Task<SteamUiCommandResult> SetCardHiddenAsync(string contentId, bool hidden,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets a card: its tab and its tracking. A reinserted card is rediscovered fresh.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the card was removed from tracking.</returns>
    Task<SteamUiCommandResult> ForgetCardAsync(string contentId, CancellationToken cancellationToken = default);

    /// <summary>Names the games on a card from Steam's library; the ids alone when it did not answer.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list, also published as the card state's <see cref="SteamCardManagerState.Games" />.</returns>
    Task<LibraryCardGames> LoadCardGamesAsync(string contentId, CancellationToken cancellationToken = default);
}
