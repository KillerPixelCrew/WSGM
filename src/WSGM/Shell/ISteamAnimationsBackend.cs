using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Answers animation commands shared by Steam pages and overlay browse sessions.</summary>
/// <remarks>
///     Long-running repository, download and import work is owned by the service; a successful command can mean
///     acceptance, with completion published in state.
/// </remarks>
internal interface ISteamAnimationsBackend
{
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

    /// <summary>Fetches the repository's list again.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether repository refresh was accepted, or a refusal while a fetch is already running.</returns>
    Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Opens one movie for a closer look.</summary>
    /// <param name="id">Id in the current repository listing or installed library.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the detail selection exists and was opened.</returns>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the closer look.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after clearing the detail selection.</returns>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a listed movie into the library.</summary>
    /// <param name="id">Current repository or library listing id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the download was accepted; Busy, Notice and Error describe its eventual outcome.</returns>
    Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes a movie from the library, and from the boot if it plays there.</summary>
    /// <param name="id">Existing installed-library id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether removal succeeded; a selected movie also triggers restoration of the default choice.</returns>
    Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Starts Big Picture with a movie from the library, or Steam's own with an empty id.</summary>
    /// <param name="id">Installed-library id, or an empty string for Steam's default movie.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the choice was saved, or a validation/persistence refusal; override-write errors appear in state.</returns>
    Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken);

    /// <summary>Picks the boot movie anew from the library.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>The choice result, or a refusal when the installed library is empty.</returns>
    Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken);

    /// <summary>Turns picking the boot movie anew at WSGM's start on or off.</summary>
    /// <param name="shuffle">Whether WSGM should choose from the library on startup.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the setting was saved.</returns>
    Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken);

    /// <summary>Sets the boot movie's volume in percent of its file's.</summary>
    /// <param name="volume">Volume as a percentage from 0 through 100; 100 retains the source volume.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the setting was saved; override-write errors appear in published state.</returns>
    Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken);

    /// <summary>Copies a movie file the user chose into the library.</summary>
    /// <param name="path">Full local path of the movie to copy into the library.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether import was accepted; its completion or failure is published.</returns>
    Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Clears the notice and the error.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after clearing both status messages.</returns>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken);
}
