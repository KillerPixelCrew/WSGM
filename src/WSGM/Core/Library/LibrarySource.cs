using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Whether a title is known to have multiplayer, which decides its default input mode.</summary>
public enum MultiplayerVerdict
{
    /// <summary>Nothing said either way. Treated as single-player for the default.</summary>
    Unknown,

    /// <summary>No multiplayer capability was reported.</summary>
    SinglePlayer,

    /// <summary>A multiplayer capability was reported.</summary>
    Multiplayer
}

/// <summary>One official image a source's catalog offers for a title.</summary>
/// <param name="Asset">Which Steam capsule it fills.</param>
/// <param name="Url">Where to fetch it, always HTTPS.</param>
/// <remarks>
///     Carried from discovery rather than looked up again at apply time, because the one catalog
///     response that answers "is this a game?" and "does it have multiplayer?" carries the images
///     too, and the catalog is paced at one request every 500 ms.
/// </remarks>
public sealed record DiscoveredArtwork(ArtworkAsset Asset, string Url);

/// <summary>How a source says one of its games launches, as far as importing it is concerned.</summary>
/// <param name="Label">What to call the route on screen.</param>
/// <param name="Validated">
///     Whether a validated route exists. Without one, Steam integration is never offered, because
///     there is nothing an acknowledgement could authorise.
/// </param>
/// <param name="Evidence">Why, in one sentence the review shows.</param>
/// <remarks>
///     This is the source's evidence, not a launch argument. The shortcut carries only the title's
///     key and the chosen mode; the launcher decides the route again from the process it actually
///     starts, because a package can change after its shortcut was written.
/// </remarks>
public sealed record GameLaunch(string Label, bool Validated, string Evidence);

/// <summary>One game a source found, before anything has been decided about importing it.</summary>
/// <param name="SourceId">Which source found it.</param>
/// <param name="Key">Its stable identity within that source. The AUMID, for Xbox.</param>
/// <param name="Name">What to call it in the library.</param>
/// <param name="InstallPath">Where it is installed, for diagnostics.</param>
/// <param name="Launch">How it launches, and whether that route is validated.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence the preview shows.</param>
/// <param name="IsGame">Whether this is a game rather than an ordinary application.</param>
/// <param name="Notes">Anything else worth showing, such as an unmodelled config element.</param>
/// <param name="Artwork">The official images the catalog offered, empty when it offered none.</param>
public sealed record DiscoveredGame(
    string SourceId,
    string Key,
    string Name,
    string InstallPath,
    GameLaunch Launch,
    MultiplayerVerdict Multiplayer,
    string MultiplayerEvidence,
    bool IsGame,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DiscoveredArtwork> Artwork);

/// <summary>One launcher whose installed games the Game Library can bring into Steam.</summary>
/// <remarks>
///     <para>
///         The spine of the Game Library, in the sense Steam ROM Manager's parsers are the spine of
///         that tool: each source knows how to find one launcher's games and describe them in the
///         shape above, and everything after discovery - planning, the user's choices, review,
///         writing the shortcut, artwork - is shared and knows nothing about where a game came from.
///     </para>
///     <para>
///         Xbox is the first source. A source's identity has to stay stable across releases,
///         because every record and every stored choice is keyed by it.
///     </para>
/// </remarks>
public interface ILibrarySource
{
    /// <summary>Stable identity of this source, lower case, never reused.</summary>
    string Id { get; }

    /// <summary>What to call it in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Finds everything this source can offer.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was found, in no particular order.</returns>
    Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken);
}
