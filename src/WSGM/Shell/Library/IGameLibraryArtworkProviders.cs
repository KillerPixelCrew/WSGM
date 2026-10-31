using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The artwork providers as the Game Library asks them.</summary>
internal interface IGameLibraryArtworkProviders
{
    /// <summary>Why nothing can be asked, or null when at least one provider is ready.</summary>
    /// <returns>A user-facing setup reason, or null when at least one configured provider is ready.</returns>
    string? Unavailable();

    /// <summary>A value that changes whenever what the providers can answer might have: keys, accounts, switches.</summary>
    /// <returns>An opaque configuration fingerprint for invalidating cached candidates; do not display or log it.</returns>
    string Signature();

    string? PauseReason(string providerId)
    {
        return null;
    }

    /// <summary>The automatic match: the first provider, in preference order, that knows the title.</summary>
    /// <exception cref="ArtworkProviderException">A provider that should have been asked could not be.</exception>
    /// <param name="request">Title and content identity used for automatic provider matching.</param>
    /// <param name="skip">Provider identifiers already attempted for this title.</param>
    /// <param name="cancellationToken">Cancels provider lookups.</param>
    /// <returns>The first automatic match in provider preference order, or null when no eligible provider matches.</returns>
    Task<ArtworkGameMatch?> FindMatchAsync(GameLibraryArtworkRequest request, IReadOnlyCollection<string> skip,
        CancellationToken cancellationToken);

    /// <summary>Every ready provider's matches together, for the user fixing a match.</summary>
    /// <param name="term">Title search text.</param>
    /// <param name="cancellationToken">Cancels provider searches.</param>
    /// <returns>Combined matches from ready providers; an empty result means none matched.</returns>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string term, CancellationToken cancellationToken);

    /// <summary>One artwork type for one provider's game, static images only.</summary>
    /// <param name="asset">Artwork slot to fetch.</param>
    /// <param name="match">Provider identity and game selected by automatic or manual matching.</param>
    /// <param name="cancellationToken">Cancels the provider request.</param>
    /// <returns>One result page for the selected game, using the library’s static, nonadult image query.</returns>
    Task<ArtworkSearchResult> FetchAsync(ArtworkAsset asset, ArtworkGameMatch match,
        CancellationToken cancellationToken);
}
