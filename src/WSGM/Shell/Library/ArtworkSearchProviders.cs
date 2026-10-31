using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary><see cref="ArtworkSearch" /> over the live configuration.</summary>
/// <param name="config">Reads the current artwork configuration.</param>
internal sealed class ArtworkSearchProviders(Func<ArtworkConfig> config) : IGameLibraryArtworkProviders
{
    /// <summary>Static images, no adult ones: what a library tile can show.</summary>
    /// <remarks>Asked of the provider rather than filtered afterwards, so a page of animations is not emptied.</remarks>
    private static readonly ArtworkQuery LibraryQuery = new(Animated: false, Adult: false);

    /// <inheritdoc />
    public string? Unavailable()
    {
        var current = config();
        return ArtworkSearch.Providers.Any(provider => provider.GetStatus(current).IsReady)
            ? null
            : ArtworkSearch.Providers.Select(provider => provider.GetStatus(current).Detail)
                .FirstOrDefault(detail => detail.Length > 0) ?? "No artwork provider is set up.";
    }

    /// <inheritdoc />
    public string Signature()
    {
        return config().ProviderSignature();
    }

    public string? PauseReason(string providerId)
    {
        return ArtworkSearch.Find(providerId)?.PauseReason;
    }

    public Task<ArtworkGameMatch?> FindMatchAsync(GameLibraryArtworkRequest request, IReadOnlyCollection<string> skip,
        CancellationToken cancellationToken)
    {
        return ArtworkSearch.FindMatchAsync(
            new ArtworkGameQuery(request.Name, request.PlatformId, request.RomName, request.RomSize),
            config(), skip, cancellationToken, request.PreferredProviderId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string term, CancellationToken cancellationToken)
    {
        return ArtworkSearch.SearchGamesAsync(term, config(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ArtworkSearchResult> FetchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, CancellationToken cancellationToken)
    {
        return ArtworkSearch.GetAssetsForMatchAsync(asset, match, config(), LibraryQuery, cancellationToken);
    }
}
