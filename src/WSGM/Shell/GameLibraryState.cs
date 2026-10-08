using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One way a title can be launched, as the review offers it.</summary>
/// <param name="Id">The route's identity within the title.</param>
/// <param name="Label">What to call it.</param>
/// <param name="Follows">Whether it starts through the title's launcher, which WSGM then follows.</param>
public sealed record GameLibraryRoute(string Id, string Label, bool Follows);

/// <summary>What one artwork type of one title would get.</summary>
/// <param name="Asset">The artwork type: grid, wide, hero, logo or icon.</param>
/// <param name="Kind">
///     <c>pick</c> for an image the user chose, <c>default</c> for the one a new title starts on,
///     <c>keep</c> for an imported title whose current image stays, <c>none</c> for a slot nothing
///     will be applied to, or <c>loading</c> while candidates are still being gathered.
/// </param>
/// <param name="Thumb">The image to show, or empty.</param>
/// <param name="Provider">Who supplied it, or empty.</param>
/// <param name="Index">Its position among the candidates, from one, or zero when it is not one of them.</param>
/// <param name="Count">How many candidates there are.</param>
public sealed record GameLibraryArtworkSlot(
    string Asset,
    string Kind,
    string Thumb,
    string Provider,
    int Index,
    int Count);

/// <summary>One source in the sidebar.</summary>
/// <param name="Id">Its stable identity.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Kind"><c>launcher</c>, <c>folder</c>, <c>rom</c> or <c>manual</c>.</param>
/// <param name="Installed">Whether it was found on this machine.</param>
/// <param name="Enabled">Whether the user has it ticked.</param>
/// <param name="Detail">What was found, or why not.</param>
/// <param name="Count">How many titles the last scan found in it, or -1 when it was not scanned.</param>
/// <param name="Checked">The host-selected checkbox state, including unavailable configured sources.</param>
/// <param name="CanToggle">Whether the host permits changing the source's enabled intent.</param>
public sealed record GameLibrarySource(
    string Id,
    string Name,
    LibrarySourceKind Kind,
    bool Installed,
    bool Enabled,
    string Detail,
    int Count,
    bool Checked = false,
    bool CanToggle = false);

/// <summary>One title in the Game Library's review.</summary>
/// <remarks>
///     Compact review projection shared by Steam and the overlay. Load detailed evidence through
///     <see cref="IGameLibraryBackend.DetailsAsync" />; display labels come from the host.
/// </remarks>
/// <param name="Id">Stable identity of the title in the review: the same title keeps it across scans.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Source">The name of the source that found it.</param>
/// <param name="SourceId">That source's identity, for grouping.</param>
/// <param name="Action">What a sync would do: Add, Update, Adopt, Skip, Remove, Conflict, or Artwork.</param>
/// <param name="ActionLabel">What that action is called on screen.</param>
/// <param name="Group">
///     Which tab lists it besides All: <c>new</c>, <c>imported</c>, <c>attention</c> (changed by hand,
///     to be removed, or artwork that could not be fetched) or <c>excluded</c>.
/// </param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Selected">Whether the user has it selected.</param>
/// <param name="Selectable">Whether it may be selected at all.</param>
/// <param name="Excluded">Whether the user said not to import it.</param>
/// <param name="Editable">Whether its launch and artwork can be changed here.</param>
/// <param name="Packaged">Whether it launches through the packaged launcher, so it has an input mode rather than routes.</param>
/// <param name="Mode">Which input mode a packaged title launches with.</param>
/// <param name="CanUseSteamIntegration">Whether the overlay route is available for it at all.</param>
/// <param name="RequiresAcknowledgement">Whether choosing that route needs the risk accepted.</param>
/// <param name="Acknowledged">Whether the user has accepted it.</param>
/// <param name="LaunchLabel">How it launches now: its input mode, or its route.</param>
/// <param name="Follows">Whether it launches through its launcher, which WSGM follows.</param>
/// <param name="Routes">The command routes it can launch by; empty for a packaged title.</param>
/// <param name="Route">The command route it would launch with, or empty.</param>
/// <param name="AppId">
///     Its Steam app id: the one its record names, or the one this run's write was confirmed with.
///     Zero until it has one, and the page offers "Change artwork" only once it does.
/// </param>
/// <param name="ArtworkApplied">How many images were applied, or null before an import.</param>
/// <param name="Artwork">What each artwork type would get, in the surfaces' order.</param>
/// <param name="ArtworkStatus">pending, loading, ready, notFound, failed or unavailable.</param>
/// <param name="ArtworkDetail">Why the artwork is failed or unavailable, or empty.</param>
/// <param name="MatchName">The game the artwork providers matched it to, or empty.</param>
/// <param name="MatchFixed">Whether the user picked that match.</param>
/// <param name="SystemId">The ROM system, or empty for other sources.</param>
/// <param name="EmulatorInstallationId">The selected installation, or empty when following the system preference.</param>
/// <param name="CoreId">The selected RetroArch core.</param>
/// <param name="ManagedId">The durable pre-Steam content identity.</param>
/// <param name="Location">The friendly expected library or storage label.</param>
/// <param name="Availability">The current managed availability observation.</param>
/// <param name="ContentPath">The original backing path, retained for review.</param>
/// <param name="Arguments">The effective typed ROM argument override, or null for other sources.</param>
/// <param name="Unavailable">Whether the host currently considers its managed content unavailable.</param>
/// <param name="AvailabilityLabel">The host's user-facing description of the current availability.</param>
public sealed record GameLibraryEntry(
    string Id,
    string Name,
    string Source,
    string SourceId,
    string Action,
    string ActionLabel,
    string Group,
    string Reason,
    bool Selected,
    bool Selectable,
    bool Excluded,
    bool Editable,
    bool Packaged,
    string Mode,
    bool CanUseSteamIntegration,
    bool RequiresAcknowledgement,
    bool Acknowledged,
    string LaunchLabel,
    bool Follows,
    IReadOnlyList<GameLibraryRoute> Routes,
    string Route,
    uint AppId,
    int? ArtworkApplied,
    IReadOnlyList<GameLibraryArtworkSlot> Artwork,
    string ArtworkStatus,
    string ArtworkDetail,
    string MatchName,
    bool MatchFixed,
    string SystemId = "",
    string EmulatorInstallationId = "",
    string CoreId = "",
    string ManagedId = "",
    string Location = "",
    string Availability = "",
    string ContentPath = "",
    IReadOnlyList<string>? Arguments = null,
    bool Unavailable = false,
    string AvailabilityLabel = "");

/// <summary>The host's wording and presentation policy for managed availability.</summary>
internal static class ManagedAvailabilityPresentation
{
    internal static bool Unavailable(ManagedContentAvailability value)
    {
        return value is ManagedContentAvailability.StorageUnavailable or ManagedContentAvailability.ContentMissing
            or ManagedContentAvailability.EmulatorUnavailable or ManagedContentAvailability.Unreadable;
    }

    internal static string Label(ManagedContentAvailability value)
    {
        return value switch
        {
            ManagedContentAvailability.Available => "Available",
            ManagedContentAvailability.StorageUnavailable => "Storage disconnected",
            ManagedContentAvailability.ContentMissing => "Game files missing",
            ManagedContentAvailability.EmulatorUnavailable => "Emulator or required files unavailable",
            ManagedContentAvailability.Unreadable => "Cannot read game files",
            _ => "Not checked yet"
        };
    }
}

/// <summary>The <see cref="GameLibraryState.Phase" /> values, shared by the service and the overlay.</summary>
public static class GameLibraryPhases
{
    /// <summary>Nothing has been scanned yet.</summary>
    public const string Idle = "idle";

    /// <summary>A scan is running.</summary>
    public const string Scanning = "scanning";

    /// <summary>A scan finished and its entries can be reviewed.</summary>
    public const string Review = "review";

    /// <summary>An apply is running.</summary>
    public const string Applying = "applying";

    /// <summary>An apply finished.</summary>
    public const string Done = "done";
}

/// <summary>Everything either surface renders: the Steam page and the overlay view alike.</summary>
/// <param name="Sources">Every source, in the sidebar's order.</param>
/// <param name="Reading">The names of the sources a scan reads: installed and ticked.</param>
/// <param name="Phase">idle, scanning, review, applying or done.</param>
/// <param name="Entries">What the scan found.</param>
/// <param name="SelectedCount">How many are selected.</param>
/// <param name="Progress">How many entries of an apply are done.</param>
/// <param name="ProgressTotal">How many an apply will do.</param>
/// <param name="LauncherAvailable">Whether the packaged-game launcher is installed.</param>
/// <param name="LauncherDetail">Why it is not, when it is not.</param>
/// <param name="Loading">Whether a scan or an apply is running.</param>
/// <param name="Notice">Something worth saying that is not an error.</param>
/// <param name="Error">Why the last operation did not do what was asked.</param>
/// <param name="ArtworkPreference">Which artwork a title starts on: catalog or providers.</param>
/// <param name="CreateCollections">Whether each source's imported titles are kept in a Steam collection.</param>
/// <param name="Revision">Monotonic publication revision.</param>
/// <param name="RomSources">The configured sources shown by both editors.</param>
/// <param name="ManualSources">The editable user-authored command sources.</param>
public sealed record GameLibraryState(
    IReadOnlyList<GameLibrarySource> Sources,
    IReadOnlyList<string> Reading,
    string Phase,
    IReadOnlyList<GameLibraryEntry> Entries,
    int SelectedCount,
    int Progress,
    int ProgressTotal,
    bool LauncherAvailable,
    string? LauncherDetail = null,
    bool Loading = false,
    string? Notice = null,
    string? Error = null,
    string ArtworkPreference = "Catalog",
    bool CreateCollections = false,
    long Revision = 0,
    IReadOnlyList<RomSourceConfig>? RomSources = null,
    IReadOnlyList<ManualShortcutConfig>? ManualSources = null);

/// <summary>The installed choices needed by ROM editors, without downloader progress or release offers.</summary>
/// <param name="Choices">Compatible installations and cores per system.</param>
/// <param name="SystemPreferences">The persisted default choices.</param>
public sealed record RomEmulatorState(
    IReadOnlyList<RomSystemEmulatorChoices> Choices,
    IReadOnlyList<EmulatorSystemPreference> SystemPreferences)
{
    /// <summary>No installed emulator or system preference.</summary>
    public static RomEmulatorState Empty { get; } = new([], []);
}

/// <summary>What the review knows about one title beyond its card: the evidence behind it.</summary>
/// <param name="InstallPath">Where it is installed, or empty.</param>
/// <param name="Identity">Its identity in its source, for diagnosis.</param>
/// <param name="LaunchEvidence">Why its launch route works, and what it cannot do.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence.</param>
/// <param name="Notes">Anything else worth showing.</param>
public sealed record GameLibraryDetails(
    string InstallPath,
    string Identity,
    string LaunchEvidence,
    string Multiplayer,
    string MultiplayerEvidence,
    IReadOnlyList<string> Notes);

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

    /// <summary>Checks one title, or all titles for an empty identity, without a full source scan.</summary>
    Task<SteamUiCommandResult> RecheckAvailabilityAsync(string id, CancellationToken cancellationToken);

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

/// <summary>The answer to a command that opens a page: the route to follow.</summary>
/// <param name="Route">The route.</param>
internal sealed record GameLibraryRouteAnswer(string Route);

/// <summary>The answer to a launch change that needs the user to accept a risk first.</summary>
/// <param name="Acknowledge">Always true: ask, then send the mode with the acknowledgement.</param>
internal sealed record GameLibraryAcknowledgeAnswer(bool Acknowledge);

/// <summary>Every candidate for one title's artwork type.</summary>
/// <param name="Asset">The artwork type.</param>
/// <param name="Status">Whether gathering is still going on, and how it ended.</param>
/// <param name="Detail">Why it failed or is unavailable, or empty.</param>
/// <param name="Selected">The position of the image the slot shows, from one, or zero.</param>
/// <param name="Options">The candidates.</param>
internal sealed record GameLibraryOptionsAnswer(
    string Asset,
    string Status,
    string Detail,
    int Selected,
    IReadOnlyList<GameLibraryArtworkOption> Options);

/// <summary>One game an artwork provider matched a search to.</summary>
/// <param name="Provider">The provider's id.</param>
/// <param name="ProviderName">The provider's name, as shown.</param>
/// <param name="Id">Its id for the game.</param>
/// <param name="Name">The game's name.</param>
/// <param name="Exact">Whether the provider calls it an exact match.</param>
public sealed record GameLibraryMatchAnswer(string Provider, string ProviderName, string Id, string Name, bool Exact);

/// <summary>The games a search found.</summary>
/// <param name="Matches">The matches, exact first, from every provider.</param>
public sealed record GameLibraryMatchesAnswer(IReadOnlyList<GameLibraryMatchAnswer> Matches);

/// <summary>The outcome of moving a title to its next launch mode or route.</summary>
/// <param name="Command">Whether the change was made or refused.</param>
/// <param name="NeedsAcknowledgement">
///     Whether nothing changed because the user has to accept the risk of the next mode first.
/// </param>
public sealed record GameLibraryLaunchCycle(SteamUiCommandResult Command, bool NeedsAcknowledgement = false);

/// <summary>The outcome of an artwork game search.</summary>
/// <param name="Command">Whether the search ran or was refused.</param>
/// <param name="Matches">The games found, or null when the search was refused.</param>
public sealed record GameLibraryMatchSearch(SteamUiCommandResult Command, GameLibraryMatchesAnswer? Matches = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GameLibraryState))]
[JsonSerializable(typeof(RomSourceConfig))]
[JsonSerializable(typeof(ManualShortcutConfig))]
[JsonSerializable(typeof(EmulatorSnapshot))]
[JsonSerializable(typeof(EmulatorPageState))]
[JsonSerializable(typeof(EmulatorProgressState))]
[JsonSerializable(typeof(GameLibraryDetails))]
[JsonSerializable(typeof(GameLibraryRouteAnswer))]
[JsonSerializable(typeof(GameLibraryAcknowledgeAnswer))]
[JsonSerializable(typeof(GameLibraryOptionsAnswer))]
[JsonSerializable(typeof(GameLibraryMatchesAnswer))]
internal sealed partial class GameLibraryJsonContext : JsonSerializerContext;
