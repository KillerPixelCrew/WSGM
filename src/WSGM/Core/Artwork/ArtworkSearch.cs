using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Asks the configured artwork providers and merges what comes back.</summary>
/// <remarks>
///     <para>
///         Two rules, for two different questions. When a person is looking - the artwork page, or
///         the Game Library's "Fix match" - every ready provider is searched in parallel and the
///         results are merged, exact matches first: one provider's failure must not hide another's
///         result, and the person picks. When nobody is looking - the Game Library matching a whole
///         scan by itself - the providers are asked in preference order and the first that knows the
///         game decides: SteamGridDB knows Steam libraries, and
///         Screenscraper, a ROM database paced at one request at a time, is a fallback rather than a
///         second opinion.
///     </para>
///     <para>
///         A provider that failed is a failure in both, never an empty answer: the page says the
///         provider could not be asked. Automatic matching tries the next provider, retaining the
///         failure if none can answer. A paused provider is skipped while the remaining providers are tried.
///     </para>
/// </remarks>
internal static class ArtworkSearch
{
    /// <summary>The providers, in the order their results are preferred on a tie.</summary>
    public static IReadOnlyList<IArtworkProvider> Providers { get; } =
        [new SteamGridDbProvider(), new ScreenscraperProvider()];


    /// <summary>Finds the provider with this id, or null.</summary>
    /// <param name="providerId">A provider id previously returned in a result.</param>
    /// <returns>The provider, or null when no provider claims that id.</returns>
    public static IArtworkProvider? Find(string providerId)
    {
        return Providers.FirstOrDefault(p => p.Id == providerId);
    }

    /// <summary>Searches every ready provider for games matching a title, for a person to pick from.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Matches from every provider that answered, exact matches first.</returns>
    public static Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, ArtworkConfig config, CancellationToken cancellationToken = default)
    {
        return SearchGamesAsync(term, config, cancellationToken, Providers);
    }

    /// <inheritdoc cref="SearchGamesAsync(string, ArtworkConfig, CancellationToken)" />
    /// <param name="term">The search term.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <param name="providers">Ordered provider set used for this request.</param>
    internal static async Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, ArtworkConfig config, CancellationToken cancellationToken,
        IReadOnlyList<IArtworkProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var ready = providers.Where(p => p.GetStatus(config).IsReady).ToArray();
        var results = await Task.WhenAll(ready.Select(async provider =>
        {
            var (matches, failure) = await TrySearchAsync(provider, term, config, cancellationToken)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                Log.Warn($"Artwork provider {provider.Id} title search failed: {failure.Message}");
            }

            return matches;
        })).ConfigureAwait(false);

        // Exact matches first, then provider declaration order, then the provider's own ranking.
        return
        [
            .. results
                .SelectMany((matches, index) => matches.Select(match => (match, index)))
                .OrderByDescending(pair => pair.match.Exact)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.match)
        ];
    }

    /// <summary>Matches imported content using its source's provider and explicit ROM system first.</summary>
    public static Task<ArtworkGameMatch?> FindMatchAsync(ArtworkGameQuery query, ArtworkConfig config,
        IReadOnlyCollection<string> skip, CancellationToken cancellationToken,
        string preferredProviderId = "steamgriddb")
    {
        return FindMatchAsync(query, config, skip, cancellationToken, preferredProviderId, Providers);
    }

    internal static async Task<ArtworkGameMatch?> FindMatchAsync(ArtworkGameQuery query, ArtworkConfig config,
        IReadOnlyCollection<string> skip, CancellationToken cancellationToken, string preferredProviderId,
        IReadOnlyList<IArtworkProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(skip);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentOutOfRangeException.ThrowIfNegative(query.SystemId);
        ArgumentOutOfRangeException.ThrowIfNegative(query.RomSize);
        if (string.IsNullOrWhiteSpace(query.Name))
        {
            return null;
        }

        ArtworkProviderException? failure = null;
        var answered = false;
        foreach (var provider in providers.OrderBy(provider => provider.Id == preferredProviderId ? 0 : 1))
        {
            if (skip.Contains(provider.Id) || !provider.GetStatus(config).IsReady)
            {
                continue;
            }

            var (matches, problem) = await TrySearchAsync(provider, query, config, cancellationToken)
                .ConfigureAwait(false);
            answered |= problem is null;
            if (problem is not null && (failure is null || problem.Paused))
            {
                failure = problem;
            }

            if ((matches.FirstOrDefault(match => match.Exact) ?? matches.FirstOrDefault()) is { } found)
            {
                return found;
            }
        }

        if (!answered && failure is not null)
        {
            throw failure;
        }

        return null;
    }

    /// <summary>Downloads one image, through the provider that serves it when one does.</summary>
    /// <param name="url">The image's address.</param>
    /// <param name="config">The loaded configuration, for a provider's credentials.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The image's bytes.</returns>
    /// <exception cref="ArtworkProviderException">The image could not be downloaded.</exception>
    /// <remarks>
    ///     An image on a provider's own API counts against the same allowance as its searches, so it goes
    ///     through the same gate; one on an open content network is fetched directly.
    /// </remarks>
    public static Task<byte[]> DownloadAsync(string url, ArtworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && Providers.FirstOrDefault(provider => provider.Serves(uri)) is { } owner
            ? owner.DownloadAsync(url, config, cancellationToken)
            : ArtworkDownload.GetAsync(url, cancellationToken);
    }

    /// <summary>Searches one provider, answering its failure instead of throwing it.</summary>
    private static Task<(IReadOnlyList<ArtworkGameMatch> Matches, ArtworkProviderException? Failure)> TrySearchAsync(
        IArtworkProvider provider, string term, ArtworkConfig config, CancellationToken cancellationToken)
    {
        return TrySearchAsync(provider, new ArtworkGameQuery(term), config, cancellationToken);
    }

    private static async Task<(IReadOnlyList<ArtworkGameMatch> Matches, ArtworkProviderException? Failure)>
        TrySearchAsync(
            IArtworkProvider provider, ArtworkGameQuery query, ArtworkConfig config,
            CancellationToken cancellationToken)
    {
        try
        {
            if (provider.PauseReason is { } pause)
            {
                return ([], new ArtworkProviderException(pause, provider.Id, true));
            }

            return (await provider.SearchGamesAsync(query, config, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtworkProviderException ex)
        {
            return ([], new ArtworkProviderException(ex.Message, provider.Id, ex.Paused));
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork provider {provider.Id} title search failed: {ex.Message}");
            return ([], new ArtworkProviderException($"{provider.DisplayName} could not be reached.", provider.Id));
        }
    }

    /// <summary>Searches every ready provider for one filtered result page.</summary>
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="steamAppId">Steam app identity to resolve.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The filtered candidates and paging status; merged results retain provider failures separately.</returns>
    public static Task<ArtworkSearchResult> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsForSteamAppAsync(asset, steamAppId, config, query, cancellationToken, Providers);
    }

    /// <inheritdoc cref="GetAssetsForSteamAppAsync(ArtworkAsset, long, ArtworkConfig, ArtworkQuery, CancellationToken)" />
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="steamAppId">Steam app identity to resolve.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="providers">Ordered provider set used for this request.</param>
    internal static Task<ArtworkSearchResult> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken, IReadOnlyList<IArtworkProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GatherAsync(config, (provider, token) =>
                provider.GetAssetsForSteamAppAsync(asset, steamAppId, config, query, token), cancellationToken,
            selectedProviders: providers);
    }

    /// <summary>Searches the issuing provider for one filtered result page.</summary>
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="match">Chosen game and the provider that issued its identity.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The filtered candidates and paging status; merged results retain provider failures separately.</returns>
    public static Task<ArtworkSearchResult> GetAssetsForMatchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsForMatchAsync(asset, match, config, query, cancellationToken, Providers);
    }

    /// <inheritdoc
    ///     cref="GetAssetsForMatchAsync(ArtworkAsset, ArtworkGameMatch, ArtworkConfig, ArtworkQuery, CancellationToken)" />
    /// <param name="asset">Requested artwork slot.</param>
    /// <param name="match">Chosen game and the provider that issued its identity.</param>
    /// <param name="config">Current provider credentials and enablement.</param>
    /// <param name="query">Zero-based page and content filters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="providers">Ordered provider set used for this request.</param>
    internal static Task<ArtworkSearchResult> GetAssetsForMatchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken, IReadOnlyList<IArtworkProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(query);
        var provider = providers.FirstOrDefault(candidate => candidate.Id == match.ProviderId);
        return provider is null
            ? Task.FromResult(new ArtworkSearchResult([], []))
            : GatherAsync(config, (p, token) =>
                p.GetAssetsForGameAsync(asset, match.Id, config, query, token), cancellationToken, provider, providers);
    }

    private static async Task<ArtworkSearchResult> GatherAsync(
        ArtworkConfig config,
        Func<IArtworkProvider, CancellationToken, Task<ArtworkPage>> fetch,
        CancellationToken cancellationToken,
        IArtworkProvider? only = null, IReadOnlyList<IArtworkProvider>? selectedProviders = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var providers = only is null ? selectedProviders ?? Providers : [only];
        var statuses = providers.Select(p => p.GetStatus(config)).ToArray();

        var answers = await Task.WhenAll(providers.Select(async (provider, index) =>
        {
            if (!statuses[index].IsReady)
            {
                return (Candidates: [], HasMore: false, Failure: null);
            }

            try
            {
                var page = await fetch(provider, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ArtworkCandidate> candidates =
                [
                    .. page.Candidates.Select(candidate => candidate with
                    {
                        ProviderId = provider.Id,
                        ProviderName = provider.DisplayName
                    })
                ];
                return (Candidates: candidates, page.HasMore, Failure: null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArtworkProviderException ex)
            {
                return (Candidates: [], HasMore: false, Failure: (string?)ex.Message);
            }
            catch (Exception ex)
            {
                Log.Warn($"Artwork provider {provider.Id} failed: {ex.Message}");
                return (
                    Candidates: (IReadOnlyList<ArtworkCandidate>)[],
                    HasMore: false,
                    Failure: (string?)$"{provider.DisplayName} could not be reached.");
            }
        })).ConfigureAwait(false);

        // De-duplicated by URL: the same asset genuinely does come back from more than one source,
        // and the picker showing it twice is the noise this issue asks to avoid. The earlier
        // provider wins, which is what makes the declaration order a preference rather than decoration.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = answers
            .SelectMany(answer => answer.Candidates)
            .Where(candidate => seen.Add(candidate.Url))
            .ToList();

        var outcomes = providers
            .Select((provider, index) => new ArtworkProviderOutcome(
                provider.DisplayName, statuses[index], answers[index].Failure, provider.Id))
            .ToArray();
        return new ArtworkSearchResult(candidates, outcomes, answers.Any(answer => answer.HasMore));
    }
}
