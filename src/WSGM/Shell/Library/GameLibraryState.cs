using System.Collections.Generic;
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
/// <param name="Location">The card, disk or library holding its files, by name.</param>
/// <param name="Availability">
///     The tracker's state for its shortcut: Available, LibraryMissing or FileMissing; empty while unchecked.
/// </param>
/// <param name="ContentPath">The original backing path, retained for review.</param>
/// <param name="Arguments">The effective typed ROM argument override, or null for other sources.</param>
/// <param name="Unavailable">Whether its shortcut cannot start right now.</param>
/// <param name="AvailabilityLabel">The host's label for that state, or empty while unchecked.</param>
/// <param name="LibraryId">The backing card library identity, or empty for device-only content.</param>
/// <param name="AvailabilityDetail">What to do when it is unavailable.</param>
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
    string AvailabilityLabel = "",
    string LibraryId = "",
    string AvailabilityDetail = "");

/// <summary>One card library this device knows or sees, with what can be done about it now.</summary>
/// <param name="Id">The library's own UUID, as its card carries it.</param>
/// <param name="Name">The card's name, kept while it is out.</param>
/// <param name="Status">The host's short status, such as Connected, Disconnected or Needs repair.</param>
/// <param name="Detail">The current explanation, or empty.</param>
/// <param name="TitleCount">How many titles the card lists, or this device remembers while it is out.</param>
/// <param name="CanRepair">Whether Repair library can rebuild its missing or damaged game list now.</param>
/// <param name="CanAcceptBinding">Whether Use this card can make this device launch its titles from where it is now.</param>
public sealed record GameLibraryStorage(
    string Id,
    string Name,
    string Status,
    string Detail,
    int TitleCount,
    bool CanRepair,
    bool CanAcceptBinding);

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
/// <param name="StorageLibraries">Every card library known or connected, with its immediate actions.</param>
/// <param name="Shortcuts">Every tracked non-Steam shortcut, WSGM's and the user's, by location and name.</param>
/// <param name="StorageChecked">Whether the tracker has checked Steam's shortcuts at least once.</param>
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
    IReadOnlyList<ManualShortcutConfig>? ManualSources = null,
    IReadOnlyList<GameLibraryStorage>? StorageLibraries = null,
    IReadOnlyList<ShortcutStatus>? Shortcuts = null,
    bool StorageChecked = false);

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
