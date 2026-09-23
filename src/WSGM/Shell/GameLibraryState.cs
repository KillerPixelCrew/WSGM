using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>One title in the Game Library's review.</summary>
/// <param name="Id">Opaque identity for this publication, not the title's own.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Source">The name of the source that found it.</param>
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
/// <param name="ArtworkApplied">How many of them were applied, or null before an import.</param>
/// <param name="Notes">Anything else worth showing.</param>
public sealed record GameLibraryEntry(
    string Id,
    string Name,
    string Source,
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
    int? ArtworkApplied);

/// <summary>Everything either surface renders: the Steam page and the overlay view alike.</summary>
/// <param name="Sources">The names of the sources a scan reads, in order.</param>
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
/// <param name="Revision">Monotonic publication revision.</param>
public sealed record GameLibraryState(
    IReadOnlyList<string> Sources,
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
}

/// <summary>Where the overlay hands the user over to inside Steam.</summary>
/// <param name="ArtworkAppId">The shortcut whose artwork page to open, or zero for the library page.</param>
/// <param name="ArtworkTitle">That title's name, for a shortcut Steam has not listed yet.</param>
internal sealed record GameLibrarySteamTarget(uint ArtworkAppId = 0, string ArtworkTitle = "")
{
    /// <summary>The Game Library's own page.</summary>
    internal static GameLibrarySteamTarget Library { get; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GameLibraryState))]
internal sealed partial class GameLibraryJsonContext : JsonSerializerContext;
