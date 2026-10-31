using System.Collections.Generic;

namespace WSGM.Shell;

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
