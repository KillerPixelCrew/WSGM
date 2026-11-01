using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One artwork source.</summary>
/// <remarks>
///     Owns endpoints, authentication, paging, pacing and error translation. Applying selected bytes
///     belongs to SteamArtwork; providers return neutral search data to both presentation surfaces.
/// </remarks>
internal interface IArtworkProvider
{
    /// <summary>Stable identifier, used in attribution and configuration.</summary>
    string Id { get; }

    /// <summary>The name shown to the user as the source of a result.</summary>
    string DisplayName { get; }

    /// <summary>Why further requests are paused, or null when they may proceed.</summary>
    string? PauseReason => null;

    /// <summary>Whether this provider can be searched with the current configuration.</summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>The provider's readiness and, when it is not ready, why.</returns>
    ArtworkProviderStatus GetStatus(ArtworkConfig config);

    /// <summary>Searches the provider for games matching a title.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="config">The loaded configuration, for credentials.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matches, best first.</returns>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, ArtworkConfig config, CancellationToken cancellationToken);

    /// <summary>Searches content using the identity this provider supports.</summary>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        ArtworkGameQuery query, ArtworkConfig config, CancellationToken cancellationToken)
    {
        return SearchGamesAsync(query.Name, config, cancellationToken);
    }

    /// <summary>Lists a filtered page for a provider-issued game id.</summary>
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="gameId">Provider-issued game identity.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The filtered candidates and paging status; provider failures must not masquerade as an empty successful page.</returns>
    Task<ArtworkPage> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken);

    /// <summary>Lists a filtered page for a Steam app id.</summary>
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="steamAppId">Steam app identity to resolve.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The filtered candidates and paging status; provider failures must not masquerade as an empty successful page.</returns>
    Task<ArtworkPage> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken);

    /// <summary>Forgets every remembered answer and failure, for when the credentials changed.</summary>
    void ResetCache();

    /// <summary>Whether an image URL is one this provider has to download itself.</summary>
    /// <param name="uri">The image's address.</param>
    /// <returns>True for an image served by the provider's own API, which paces and authenticates it.</returns>
    /// <remarks>An image on an open content network is downloaded by anyone, without the provider.</remarks>
    bool Serves(Uri uri)
    {
        return false;
    }

    /// <summary>Downloads one image this provider serves, through its own pacing and credentials.</summary>
    /// <param name="url">The image's address, as the provider answered it.</param>
    /// <param name="config">The loaded configuration, for credentials.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The image's bytes.</returns>
    /// <exception cref="ArtworkProviderException">The image could not be downloaded.</exception>
    Task<byte[]> DownloadAsync(string url, ArtworkConfig config, CancellationToken cancellationToken)
    {
        return ArtworkDownload.GetAsync(url, cancellationToken);
    }
}
