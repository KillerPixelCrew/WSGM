using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>One way a title can be launched, as the review offers it.</summary>
/// <param name="Id">The route's identity within the title.</param>
/// <param name="Label">What to call it.</param>
/// <param name="Evidence">Why it works and what it cannot do.</param>
public sealed record GameLibraryRoute(string Id, string Label, string Evidence);

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
/// <param name="Kind"><c>launcher</c> or <c>folder</c>.</param>
/// <param name="Installed">Whether it was found on this machine.</param>
/// <param name="Enabled">Whether the user has it ticked.</param>
/// <param name="Detail">What was found, or why not.</param>
/// <param name="Count">How many titles the last scan found in it, or -1 when it was not scanned.</param>
public sealed record GameLibrarySource(
    string Id,
    string Name,
    string Kind,
    bool Installed,
    bool Enabled,
    string Detail,
    int Count);

/// <summary>One title in the Game Library's review.</summary>
/// <param name="Id">Opaque identity for this publication, not the title's own.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Source">The name of the source that found it.</param>
/// <param name="SourceId">That source's identity, for grouping.</param>
/// <param name="Identity">Its identity in that source, shown for diagnosis.</param>
/// <param name="InstallPath">Where it is installed.</param>
/// <param name="LaunchLabel">What its launch route is called.</param>
/// <param name="LaunchValidated">Whether that route is validated.</param>
/// <param name="LaunchEvidence">Why, in one sentence.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence.</param>
/// <param name="Mode">Which route it would launch with.</param>
/// <param name="CanUseSteamIntegration">Whether the overlay route is available for it at all.</param>
/// <param name="RequiresAcknowledgement">Whether choosing that route needs the risk accepted.</param>
/// <param name="Acknowledged">Whether the user has accepted it.</param>
/// <param name="Action">What a sync would do.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Selected">Whether the user has it selected.</param>
/// <param name="Selectable">Whether it may be selected at all.</param>
/// <param name="Excluded">Whether the user said not to import it.</param>
/// <param name="AppId">
///     Its Steam app id: the one its record names, or the one this run's write was confirmed with.
///     Zero until it has one, and the page offers "Change artwork" only once it does.
/// </param>
/// <param name="ArtworkOffered">How many images the source's catalog offered.</param>
/// <param name="ArtworkApplied">How many images were applied, or null before an import.</param>
/// <param name="Notes">Anything else worth showing.</param>
/// <param name="Routes">The command routes it can launch by; empty for an Xbox title.</param>
/// <param name="Route">The command route it would launch with, or empty.</param>
/// <param name="Artwork">What each artwork type would get, in the surfaces' order.</param>
/// <param name="ArtworkStatus">pending, loading, ready or failed.</param>
/// <param name="MatchName">The game the artwork providers matched it to, or empty.</param>
/// <param name="MatchFixed">Whether the user picked that match.</param>
public sealed record GameLibraryEntry(
    string Id,
    string Name,
    string Source,
    string SourceId,
    string Identity,
    string InstallPath,
    string LaunchLabel,
    bool LaunchValidated,
    string LaunchEvidence,
    string Multiplayer,
    string MultiplayerEvidence,
    string Mode,
    bool CanUseSteamIntegration,
    bool RequiresAcknowledgement,
    bool Acknowledged,
    string Action,
    string Reason,
    bool Selected,
    bool Selectable,
    bool Excluded,
    IReadOnlyList<string> Notes,
    uint AppId,
    int ArtworkOffered,
    int? ArtworkApplied,
    IReadOnlyList<GameLibraryRoute> Routes,
    string Route,
    IReadOnlyList<GameLibraryArtworkSlot> Artwork,
    string ArtworkStatus,
    string MatchName,
    bool MatchFixed);

/// <summary>Everything either surface renders: the Steam page and the overlay view alike.</summary>
/// <param name="Sources">Every source, in the sidebar's order.</param>
/// <param name="Phase">idle, scanning, review, applying or done.</param>
/// <param name="Entries">What the scan found.</param>
/// <param name="SelectedCount">How many are selected.</param>
/// <param name="AddCount">How many would be created.</param>
/// <param name="UpdateCount">How many would be rewritten.</param>
/// <param name="RemoveCount">How many would be deleted.</param>
/// <param name="SkipCount">How many need nothing.</param>
/// <param name="ConflictCount">How many were changed by hand.</param>
/// <param name="UnroutableCount">How many have no validated launch route.</param>
/// <param name="Progress">How many entries of an apply are done.</param>
/// <param name="ProgressTotal">How many an apply will do.</param>
/// <param name="LauncherAvailable">Whether the packaged-game launcher is installed.</param>
/// <param name="LauncherDetail">Why it is not, when it is not.</param>
/// <param name="Loading">Whether work is in flight.</param>
/// <param name="Notice">Something worth saying that is not an error.</param>
/// <param name="Error">Why the last operation did not do what was asked.</param>
/// <param name="ArtworkPreference">Which artwork a title starts on: catalog or providers.</param>
/// <param name="Revision">Monotonic publication revision.</param>
public sealed record GameLibraryState(
    IReadOnlyList<GameLibrarySource> Sources,
    string Phase,
    IReadOnlyList<GameLibraryEntry> Entries,
    int SelectedCount,
    int AddCount,
    int UpdateCount,
    int RemoveCount,
    int SkipCount,
    int ConflictCount,
    int UnroutableCount,
    int Progress,
    int ProgressTotal,
    bool LauncherAvailable,
    string? LauncherDetail = null,
    bool Loading = false,
    string? Notice = null,
    string? Error = null,
    string ArtworkPreference = "Catalog",
    long Revision = 0);

/// <summary>The Game Library's operations, as both surfaces invoke them.</summary>
public interface IGameLibraryBackend
{
    /// <summary>Scans the source. Writes nothing.</summary>
    Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Cancels a scan or an apply in progress.</summary>
    Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken);

    /// <summary>Selects or deselects one entry.</summary>
    Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken);

    /// <summary>Selects or deselects everything that may be selected.</summary>
    Task<SteamUiCommandResult> SelectAllAsync(bool selected, CancellationToken cancellationToken);

    /// <summary>Changes one entry's launch mode.</summary>
    /// <remarks>
    ///     The acknowledgement is checked here, in the host, not only in the page: a page defect
    ///     must not be able to put a multiplayer title on the injection route.
    /// </remarks>
    Task<SteamUiCommandResult> SetModeAsync(
        string id, string mode, bool acknowledged, CancellationToken cancellationToken);

    /// <summary>Leaves a title out of this and every later scan until the user offers it again.</summary>
    Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Offers a left-out title again.</summary>
    Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Opens the artwork page for an entry's shortcut, answering with the route to show.</summary>
    Task<SteamUiCommandResult> OpenArtworkAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies the selected entries.</summary>
    Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken);

    /// <summary>Ticks or unticks a source. An unticked source is not scanned.</summary>
    Task<SteamUiCommandResult> SetSourceEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);

    /// <summary>Adds a shortcuts folder as a source.</summary>
    Task<SteamUiCommandResult> AddFolderAsync(string path, bool includeSubfolders, CancellationToken cancellationToken);

    /// <summary>Removes a shortcuts folder. Its imported titles stay in Steam.</summary>
    Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken);

    /// <summary>Changes one entry's command route.</summary>
    Task<SteamUiCommandResult> SetRouteAsync(string id, string route, CancellationToken cancellationToken);

    /// <summary>Moves one entry's artwork of one type to the next or previous candidate.</summary>
    Task<SteamUiCommandResult> CycleArtworkAsync(
        string id, string asset, int delta, CancellationToken cancellationToken);

    /// <summary>Picks one candidate, by its URL, for one entry's artwork type.</summary>
    Task<SteamUiCommandResult> PickArtworkAsync(
        string id, string asset, string url, CancellationToken cancellationToken);

    /// <summary>Clears one entry's artwork type, so nothing is applied to it.</summary>
    Task<SteamUiCommandResult> ClearArtworkAsync(string id, string asset, CancellationToken cancellationToken);

    /// <summary>Fills the selected entries' artwork from one kind of provider.</summary>
    /// <param name="preference">catalog or providers.</param>
    /// <param name="onlyEmpty">Whether to leave slots the user already picked alone.</param>
    /// <param name="asset">One artwork type, or empty for all of them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<SteamUiCommandResult> FillArtworkAsync(
        string preference, bool onlyEmpty, string asset, CancellationToken cancellationToken);

    /// <summary>Drops every artwork pick of the selected entries.</summary>
    Task<SteamUiCommandResult> ResetArtworkAsync(CancellationToken cancellationToken);

    /// <summary>Asks for one entry's artwork before the others, and answers with its candidates.</summary>
    Task<SteamUiCommandResult> ArtworkOptionsAsync(string id, string asset, CancellationToken cancellationToken);

    /// <summary>Searches the artwork providers for the right game, answering with the matches.</summary>
    Task<SteamUiCommandResult> SearchMatchAsync(string id, string query, CancellationToken cancellationToken);

    /// <summary>Matches an entry to a provider's game, or back to the automatic match with an empty id.</summary>
    Task<SteamUiCommandResult> SetMatchAsync(
        string id, string provider, string gameId, string name, CancellationToken cancellationToken);
}

/// <summary>Where the overlay hands the user over to inside Steam.</summary>
/// <param name="ArtworkAppId">The shortcut whose artwork page to open, or zero for the library page.</param>
/// <param name="ArtworkTitle">That title's name, for a shortcut Steam has not listed yet.</param>
internal sealed record GameLibrarySteamTarget(uint ArtworkAppId = 0, string ArtworkTitle = "")
{
    /// <summary>The Game Library's own page.</summary>
    internal static GameLibrarySteamTarget Library { get; } = new();
}

/// <summary>The answer to a command that opens a page: the route to follow.</summary>
/// <param name="Route">The route.</param>
internal sealed record GameLibraryRouteAnswer(string Route);

/// <summary>One candidate image, as a command answers it.</summary>
/// <param name="Url">The full-size image.</param>
/// <param name="Thumb">The image to show.</param>
/// <param name="Provider">Who supplied it.</param>
/// <param name="Catalog">Whether it is the title's own source's image.</param>
/// <param name="Width">Its width, or zero.</param>
/// <param name="Height">Its height, or zero.</param>
internal sealed record GameLibraryOptionAnswer(
    string Url,
    string Thumb,
    string Provider,
    bool Catalog,
    int Width,
    int Height);

/// <summary>Every candidate for one title's artwork type.</summary>
/// <param name="Asset">The artwork type.</param>
/// <param name="Status">Whether gathering is still going on.</param>
/// <param name="Selected">The position of the image the slot shows, from one, or zero.</param>
/// <param name="Options">The candidates.</param>
internal sealed record GameLibraryOptionsAnswer(
    string Asset,
    string Status,
    int Selected,
    IReadOnlyList<GameLibraryOptionAnswer> Options);

/// <summary>One game an artwork provider matched a search to.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Id">Its id for the game.</param>
/// <param name="Name">The game's name.</param>
/// <param name="Exact">Whether the provider calls it an exact match.</param>
internal sealed record GameLibraryMatchAnswer(string Provider, string Id, string Name, bool Exact);

/// <summary>The games a search found.</summary>
/// <param name="Matches">The matches, exact first.</param>
internal sealed record GameLibraryMatchesAnswer(IReadOnlyList<GameLibraryMatchAnswer> Matches);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GameLibraryState))]
[JsonSerializable(typeof(GameLibraryRouteAnswer))]
[JsonSerializable(typeof(GameLibraryOptionsAnswer))]
[JsonSerializable(typeof(GameLibraryMatchesAnswer))]
internal sealed partial class GameLibraryJsonContext : JsonSerializerContext;
