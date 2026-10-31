using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Game Library's operations, as both surfaces invoke them.</summary>
public interface IGameLibraryBackend
{
    /// <summary>The independently cached ROM editor choices.</summary>
    RomEmulatorState ReadRomState();

    /// <summary>The reviewed and installed-core parser systems.</summary>
    IReadOnlyList<RomSystemProfile> ReadRomSystems();

    /// <summary>Adds or edits a ROM source without changing its existing identity.</summary>
    Task<SteamUiCommandResult> AddRomSourceAsync(RomSourceConfig source, CancellationToken cancellationToken);

    /// <summary>Removes source configuration while retaining imported shortcuts for explicit cleanup.</summary>
    Task<SteamUiCommandResult> RemoveRomSourceAsync(string id, CancellationToken cancellationToken);

    /// <summary>Adds or edits an authored command source.</summary>
    Task<SteamUiCommandResult> AddManualSourceAsync(ManualShortcutConfig source, CancellationToken cancellationToken);

    /// <summary>Removes an authored source without deleting its backing content.</summary>
    Task<SteamUiCommandResult> RemoveManualSourceAsync(string id, CancellationToken cancellationToken);

    /// <summary>Changes a title's emulator and core, or clears its override with empty identities.</summary>
    Task<SteamUiCommandResult> SetRomEmulatorAsync(string id, string installationId, string coreId,
        CancellationToken cancellationToken);

    /// <summary>Changes the source title used by import and artwork identification.</summary>
    Task<SteamUiCommandResult> SetRomTitleAsync(string id, string name, CancellationToken cancellationToken);

    /// <summary>Sets typed per-title arguments, or clears them to the source default.</summary>
    Task<SteamUiCommandResult> SetRomArgumentsAsync(string id, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>Explicitly selects or cancels removal of an unchanged managed shortcut.</summary>
    Task<SteamUiCommandResult> SetCleanupAsync(string id, bool cleanup, CancellationToken cancellationToken);

    /// <summary>Checks every shortcut and card again now, answering when that pass has finished.</summary>
    Task<SteamUiCommandResult> RecheckAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Rebuilds a connected card's missing or damaged game list from the titles WSGM knows, immediately.</summary>
    /// <param name="id">The card library's UUID.</param>
    /// <param name="cancellationToken">Cancels request admission.</param>
    Task<SteamUiCommandResult> RepairLibraryAsync(string id, CancellationToken cancellationToken);

    /// <summary>Makes this device launch a connected card's titles from the volume it is on now, immediately.</summary>
    /// <param name="id">The card library's UUID.</param>
    /// <param name="cancellationToken">Cancels request admission.</param>
    Task<SteamUiCommandResult> UseLibraryAsync(string id, CancellationToken cancellationToken);

    /// <summary>Scans the ticked sources. Writes nothing to Steam.</summary>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether a background scan was started. Completion does not wait for scan results; observe GameLibraryState.</returns>
    Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Cancels a scan or an apply in progress.</summary>
    /// <param name="cancellationToken">Cancels this request before cancellation of the active work is requested.</param>
    /// <returns>Acknowledgement of the cancellation request; the active scan or apply may still be unwinding.</returns>
    Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken);

    /// <summary>Selects or deselects one entry.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken);

    /// <summary>Selects or deselects what a surface shows.</summary>
    /// <param name="group">The group its tab shows (<see cref="GameLibraryEntry.Group" />), or empty for all.</param>
    /// <param name="query">Its search, matched against names ignoring case, or empty.</param>
    /// <param name="selected">Whether to select or deselect them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    ///     By group and search rather than by id, so a library of any size fits one request. Selecting
    ///     never ticks a removal, an add Steam may already have, or a title the user deleted from
    ///     Steam: those are asked for one at a time. Deselecting clears everything shown.
    /// </remarks>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> SelectAsync(
        string group, string query, bool selected, CancellationToken cancellationToken);

    /// <summary>Sets listed entries to selected or not selected in one publication.</summary>
    /// <param name="ids">The entries to set; ids no longer present are skipped.</param>
    /// <param name="selected">The state to set. An entry that cannot be selected stays unselected.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    ///     Sets rather than toggles, so an entry changed meanwhile on another surface still ends in
    ///     the requested state.
    /// </remarks>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> SetSelectedAsync(
        IReadOnlyList<string> ids, bool selected, CancellationToken cancellationToken);

    /// <summary>Changes one entry's launch mode.</summary>
    /// <remarks>
    ///     The acknowledgement is checked here, in the host, not only in the page: a page defect
    ///     must not be able to put a multiplayer title on the injection route.
    /// </remarks>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="mode">ImportMode name, matched without case sensitivity.</param>
    /// <param name="acknowledged">Whether the user accepted the multiplayer injection risk, if required for this mode.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> SetModeAsync(
        string id, string mode, bool acknowledged, CancellationToken cancellationToken);

    /// <summary>Moves one entry to its next launch mode or route.</summary>
    /// <remarks>
    ///     Answers <see cref="GameLibraryLaunchCycle.NeedsAcknowledgement" /> without changing anything
    ///     when the next mode is the Steam overlay on a multiplayer title, so the surface asks the user
    ///     to accept the risk and then sends <see cref="SetModeAsync" /> with the acknowledgement.
    /// </remarks>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>The command outcome and whether explicit acknowledgement is required before changing the launch mode.</returns>
    Task<GameLibraryLaunchCycle> CycleLaunchAsync(string id, CancellationToken cancellationToken);

    /// <summary>Leaves a title out of this and every later scan until the user offers it again.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether exclusion was persisted; already imported titles cannot be excluded this way.</returns>
    Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Offers a left-out title again.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the persistent exclusion was removed.</returns>
    Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Answers the evidence behind one title (<see cref="GameLibraryDetails" />).</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels the request before reading the review.</param>
    /// <returns>GameLibraryDetails as the result value, or a refusal if the entry is no longer listed.</returns>
    Task<SteamUiCommandResult> DetailsAsync(string id, CancellationToken cancellationToken);

    /// <summary>Opens the artwork page for an entry's shortcut, answering with the route to show.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="cancellationToken">Cancels preparation of the artwork page.</param>
    /// <returns>
    ///     The navigation route as the result value, or a refusal when no imported shortcut or artwork backend is
    ///     available.
    /// </returns>
    Task<SteamUiCommandResult> OpenArtworkAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies the selected entries.</summary>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether a background import was started. Observe GameLibraryState for per-entry progress and failures.</returns>
    Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken);

    /// <summary>Ticks or unticks a source. An unticked source is not scanned.</summary>
    /// <param name="id">Source identifier from GameLibrarySource.Id.</param>
    /// <param name="enabled">True to enable and request a scan; false to remove its entries from the review.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the preference was saved. An enabled source may be scanned asynchronously.</returns>
    Task<SteamUiCommandResult> SetSourceEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    ///     Turns the per-source Steam collections on or off. On brings the titles already imported into
    ///     them at once; off leaves the collections already made as they are.
    /// </summary>
    /// <param name="enabled">Whether imported titles should join per-source collections. False preserves existing collections.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the preference was saved. Collection synchronization runs in the background.</returns>
    Task<SteamUiCommandResult> SetCollectionsAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Adds a shortcuts folder as a source.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="includeSubfolders">Whether its subfolders are read too.</param>
    /// <param name="extensions">The file types it offers: some of .lnk, .url and .exe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     Whether the folder preference was saved and a scan requested; rejects missing, duplicate or network folders
    ///     and unsupported extensions.
    /// </returns>
    Task<SteamUiCommandResult> AddFolderAsync(
        string path, bool includeSubfolders, IReadOnlyList<string> extensions, CancellationToken cancellationToken);

    /// <summary>Removes a shortcuts folder. Its imported titles stay in Steam until the user removes them.</summary>
    /// <param name="id">Folder-source identifier from GameLibrarySource.Id.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the folder source was removed; existing Steam shortcuts are retained.</returns>
    Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken);

    /// <summary>Changes one entry's command route.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="route">Route identifier from the entry’s Routes collection.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> SetRouteAsync(string id, string route, CancellationToken cancellationToken);

    /// <summary>Moves one entry's artwork of one type to the next or previous candidate.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <param name="delta">Candidate offset; use 1 for next or -1 for previous.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> CycleArtworkAsync(
        string id, string asset, int delta, CancellationToken cancellationToken);

    /// <summary>Picks one candidate, by its URL, for one entry's artwork type.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <param name="url">URL of a candidate offered by ArtworkOptionsAsync.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> PickArtworkAsync(
        string id, string asset, string url, CancellationToken cancellationToken);

    /// <summary>Clears one entry's artwork type, so nothing is applied to it.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the review change was accepted, with a refusal reason on failure.</returns>
    Task<SteamUiCommandResult> ClearArtworkAsync(string id, string asset, CancellationToken cancellationToken);

    /// <summary>Fills the selected entries' artwork from one kind of provider.</summary>
    /// <param name="preference">catalog or providers.</param>
    /// <param name="onlyEmpty">Whether to leave slots that already show or keep an image alone.</param>
    /// <param name="asset">One artwork type, or empty for all of them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     Whether loaded candidates were selected into the review picks. This does not change the saved provider
    ///     preference or start new lookups.
    /// </returns>
    Task<SteamUiCommandResult> FillArtworkAsync(
        string preference, bool onlyEmpty, string asset, CancellationToken cancellationToken);

    /// <summary>Drops every artwork pick of the selected entries.</summary>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether selected entries were reset to their default artwork choices.</returns>
    Task<SteamUiCommandResult> ResetArtworkAsync(CancellationToken cancellationToken);

    /// <summary>Asks for one entry's artwork before the others, and answers with its candidates.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <param name="cancellationToken">Cancels the request before reading or prioritizing candidates.</param>
    /// <returns>
    ///     The slot’s candidates and loading status as the result value, or a refusal. Pending lookups are prioritized,
    ///     not awaited.
    /// </returns>
    Task<SteamUiCommandResult> ArtworkOptionsAsync(string id, string asset, CancellationToken cancellationToken);

    /// <summary>Searches every artwork provider for the right game, answering with the matches.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="query">Title search text sent to the artwork providers.</param>
    /// <param name="cancellationToken">Cancels the provider search.</param>
    /// <returns>Provider matches and any refusal or provider failure detail.</returns>
    Task<GameLibraryMatchSearch> SearchMatchAsync(string id, string query, CancellationToken cancellationToken);

    /// <summary>Matches an entry to a provider's game, or back to the automatic match with an empty id.</summary>
    /// <param name="id">Stable review entry identifier from GameLibraryEntry.Id.</param>
    /// <param name="provider">Artwork provider identifier; ignored when gameId is empty.</param>
    /// <param name="gameId">Provider game identifier, or empty to restore automatic matching.</param>
    /// <param name="name">Display name of the selected provider match.</param>
    /// <param name="cancellationToken">Cancels request admission; accepted background work uses the owning service’s lifetime.</param>
    /// <returns>Whether the match was accepted; artwork candidates are then gathered again.</returns>
    Task<SteamUiCommandResult> SetMatchAsync(
        string id, string provider, string gameId, string name, CancellationToken cancellationToken);
}
