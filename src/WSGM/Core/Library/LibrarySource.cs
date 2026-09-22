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

/// <summary>One game a source found, before anything has been decided about importing it.</summary>
/// <param name="SourceId">Which source found it.</param>
/// <param name="Key">Its stable identity within that source. The AUMID, for Xbox.</param>
/// <param name="Name">What to call it in the library.</param>
/// <param name="InstallPath">Where it is installed, for diagnostics.</param>
/// <param name="Runtime">Which launch route it needs.</param>
/// <param name="RuntimeEvidence">Why, in one sentence the preview shows.</param>
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
    XboxRuntime Runtime,
    string RuntimeEvidence,
    MultiplayerVerdict Multiplayer,
    string MultiplayerEvidence,
    bool IsGame,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DiscoveredArtwork> Artwork);

/// <summary>Somewhere games can be imported from.</summary>
/// <remarks>
///     One interface with one implementation today. It exists because the feature is specified to
///     grow ROM, folder and third-party launcher sources, and the discovery half is the part that
///     differs between them. It is deliberately not a registry, a capability enumeration or a
///     manifest-declared plugin point: if the further sources are ever dropped, this should be
///     deleted and the Xbox type inlined rather than kept as scaffolding.
/// </remarks>
public interface ILibrarySource
{
    /// <summary>Stable identity of this source.</summary>
    string Id { get; }

    /// <summary>What to call it in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Finds everything this source can offer.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was found, in no particular order.</returns>
    Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken);
}
