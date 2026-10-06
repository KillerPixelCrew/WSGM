using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Why a provider cannot be searched right now.</summary>
/// <remarks>
///     Separate from "no results" on purpose. A provider that needs credentials nobody has entered
///     returns nothing, and so does a provider that searched and found nothing; showing both as an
///     empty grid tells the user their game has no artwork when the truth is that a source was never
///     asked. That distinction is the whole reason this is a first-class state.
/// </remarks>
internal enum ArtworkProviderReadiness
{
    /// <summary>Configured and usable.</summary>
    Ready,

    /// <summary>Turned off by the user.</summary>
    Disabled,

    /// <summary>Needs credentials that are not configured.</summary>
    MissingCredentials
}

/// <summary>Whether a provider can be searched, and what to say when it cannot.</summary>
/// <param name="Readiness">The provider's current state.</param>
/// <param name="Detail">A user-facing sentence when it is not <see cref="ArtworkProviderReadiness.Ready" />.</param>
internal readonly record struct ArtworkProviderStatus(ArtworkProviderReadiness Readiness, string Detail = "")
{
    /// <summary>Whether the provider may be searched.</summary>
    public bool IsReady => Readiness == ArtworkProviderReadiness.Ready;

    /// <summary>A ready status.</summary>
    public static ArtworkProviderStatus Ready { get; } = new(ArtworkProviderReadiness.Ready);
}

/// <summary>Provider-neutral artwork paging and content filters.</summary>
/// <param name="Page">Zero-based result page.</param>
/// <param name="Styles">Provider style identifiers, or null for that provider's defaults.</param>
/// <param name="Dimensions">Accepted dimensions such as <c>600x900</c>.</param>
/// <param name="Mimes">Accepted MIME types.</param>
/// <param name="Static">Whether static images are included.</param>
/// <param name="Animated">Whether animated images are included.</param>
/// <param name="Adult">Whether adult-tagged results are included.</param>
/// <param name="Humor">Whether humor-tagged results are included.</param>
/// <param name="Epilepsy">Whether flashing/epilepsy-tagged results are included.</param>
/// <param name="Untagged">Whether results without those tags are included.</param>
internal sealed record ArtworkQuery(
    int Page = 0,
    IReadOnlyList<string>? Styles = null,
    IReadOnlyList<string>? Dimensions = null,
    IReadOnlyList<string>? Mimes = null,
    bool Static = true,
    bool Animated = true,
    bool Adult = false,
    bool Humor = true,
    bool Epilepsy = true,
    bool Untagged = true);

/// <summary>One artwork candidate; the merge de-duplicates by <see cref="Url" />.</summary>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL for the picker grid.</param>
/// <param name="Width">Pixel width, or zero when the provider does not report one.</param>
/// <param name="Height">Pixel height, or zero when the provider does not report one.</param>
/// <param name="Extension">Verified static image format, <c>png</c> or <c>jpg</c>.</param>
/// <param name="ProviderId">Stable source identifier populated by the merger.</param>
/// <param name="ProviderName">Visible source attribution populated by the merger.</param>
/// <param name="Author">Artwork author, when reported.</param>
/// <param name="Style">Provider style identifier, when reported.</param>
/// <param name="Notes">Provider notes, when reported.</param>
/// <param name="Animated">Whether the result is animated.</param>
/// <param name="Nsfw">Whether the provider marks the result as adult content.</param>
/// <param name="Humor">Whether the provider marks the result as humor.</param>
/// <param name="Epilepsy">Whether the provider marks the result as flashing content.</param>
internal sealed record ArtworkCandidate(
    string Url,
    string Thumb,
    int Width,
    int Height,
    string Extension,
    string ProviderId = "",
    string ProviderName = "",
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);

/// <summary>A game a provider matched a title to.</summary>
/// <param name="ProviderId">Stable id of the provider that matched it.</param>
/// <param name="Id">The provider's own game id.</param>
/// <param name="Name">The matched name.</param>
/// <param name="Exact">Whether the provider reports this as an exact rather than fuzzy match.</param>
internal sealed record ArtworkGameMatch(string ProviderId, string Id, string Name, bool Exact);

/// <summary>One artwork source.</summary>
/// <remarks>
///     Everything source-specific lives behind this: endpoints, authentication, media vocabularies,
///     rate limits and their error shapes. What comes back out is the same for every provider, which is
///     what keeps the picker free of provider knowledge. Applying a chosen image is deliberately not
///     here — that is one Steam client call and is the same whichever source supplied the bytes.
/// </remarks>
internal interface IArtworkProvider
{
    /// <summary>Stable identifier, used in attribution and configuration.</summary>
    string Id { get; }

    /// <summary>The name shown to the user as the source of a result.</summary>
    string DisplayName { get; }

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

    /// <summary>Lists a filtered page for a provider-issued game id.</summary>
    Task<ArtworkPage> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken);

    /// <summary>Lists a filtered page for a Steam app id.</summary>
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

/// <summary>What one provider contributed to a search, including the reason it contributed nothing.</summary>
/// <param name="ProviderName">The provider's display name.</param>
/// <param name="Status">Whether it was searched at all.</param>
/// <param name="Failure">The user-facing failure, or null when the provider answered.</param>
internal sealed record ArtworkProviderOutcome(
    string ProviderName,
    ArtworkProviderStatus Status,
    string? Failure);

/// <summary>One provider page, including whether its raw answer has another page.</summary>
internal sealed record ArtworkPage(IReadOnlyList<ArtworkCandidate> Candidates, bool HasMore);

/// <summary>The merged result of asking every provider.</summary>
/// <param name="Candidates">Every candidate, ranked.</param>
/// <param name="Outcomes">One entry per provider, in declaration order.</param>
/// <param name="HasMore">Whether any provider's raw answer has another page.</param>
internal sealed record ArtworkSearchResult(
    IReadOnlyList<ArtworkCandidate> Candidates,
    IReadOnlyList<ArtworkProviderOutcome> Outcomes,
    bool HasMore = false)
{
    /// <summary>Whether every ready provider failed, which is different from finding nothing.</summary>
    /// <remarks>
    ///     An empty grid needs a reason. If no provider was even ready, or all of them faulted, the
    ///     picker must say so rather than report that the game has no artwork.
    /// </remarks>
    public bool NoProviderAnswered => !Outcomes.Any(o => o.Status.IsReady && o.Failure is null);

    /// <summary>The failures worth showing, one per provider that was asked and could not answer.</summary>
    public IReadOnlyList<string> Failures =>
        [.. Outcomes.Where(o => o.Failure is not null).Select(o => $"{o.ProviderName}: {o.Failure}")];

    /// <summary>The providers that were skipped, with the reason each was skipped.</summary>
    public IReadOnlyList<string> Skipped =>
    [
        .. Outcomes.Where(o => !o.Status.IsReady && o.Status.Detail.Length > 0)
            .Select(o => $"{o.ProviderName}: {o.Status.Detail}")
    ];
}

/// <summary>Asks the configured artwork providers and merges what comes back.</summary>
/// <remarks>
///     <para>
///         Two rules, for two different questions. When a person is looking - the artwork page, or
///         the Game Library's "Fix match" - every ready provider is searched in parallel and the
///         results are merged, exact matches first: one provider's failure must not hide another's
///         result, and the person picks. When nobody is looking - the Game Library matching a whole
///         scan by itself - the providers are asked in preference order and the first that knows the
///         game decides (<see cref="FindMatchAsync" />): SteamGridDB knows Steam libraries, and
///         Screenscraper, a ROM database paced at one request at a time, is a fallback rather than a
///         second opinion.
///     </para>
///     <para>
///         A provider that failed is a failure in both, never an empty answer: the page says the
///         provider could not be asked, and the automatic match stops rather than falling through to
///         a provider that might pin the wrong game.
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
                Log.Warn($"Artwork provider {provider.Id} title search failed: {failure}");
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

    /// <summary>Matches a title with nobody looking: the first provider, in preference order, that knows it.</summary>
    /// <param name="term">The title.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="skip">Providers already tried for this title, whose games had no artwork.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>That provider's exact match, or else its first result; null when no ready provider has one.</returns>
    /// <exception cref="ArtworkProviderException">
    ///     A provider that should have been asked could not be. The match stops there rather than
    ///     falling through, because the next provider answering would pin its guess as the match.
    /// </exception>
    public static async Task<ArtworkGameMatch?> FindMatchAsync(
        string term, ArtworkConfig config, IReadOnlyCollection<string> skip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(skip);
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        foreach (var provider in Providers)
        {
            if (skip.Contains(provider.Id) || !provider.GetStatus(config).IsReady)
            {
                continue;
            }

            var (matches, failure) = await TrySearchAsync(provider, term, config, cancellationToken)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                throw new ArtworkProviderException($"{provider.DisplayName}: {failure}");
            }

            if ((matches.FirstOrDefault(candidate => candidate.Exact) ?? matches.FirstOrDefault()) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Forgets every provider's remembered answers and failures.</summary>
    /// <remarks>
    ///     Called when an API key or account changes, so an answer fetched with the old credentials, or
    ///     the refusal they earned, is not reused for the new ones.
    /// </remarks>
    public static void ResetCaches()
    {
        foreach (var provider in Providers)
        {
            provider.ResetCache();
        }
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
    private static async Task<(IReadOnlyList<ArtworkGameMatch> Matches, string? Failure)> TrySearchAsync(
        IArtworkProvider provider, string term, ArtworkConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return (await provider.SearchGamesAsync(term, config, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtworkProviderException ex)
        {
            return ([], ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork provider {provider.Id} title search failed: {ex.Message}");
            return ([], $"{provider.DisplayName} could not be reached.");
        }
    }


    /// <summary>Searches every ready provider for one filtered result page.</summary>
    public static Task<ArtworkSearchResult> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsForSteamAppAsync(asset, steamAppId, config, query, cancellationToken, Providers);
    }

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
    public static Task<ArtworkSearchResult> GetAssetsForMatchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsForMatchAsync(asset, match, config, query, cancellationToken, Providers);
    }

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
                provider.DisplayName, statuses[index], answers[index].Failure))
            .ToArray();
        return new ArtworkSearchResult(candidates, outcomes, answers.Any(answer => answer.HasMore));
    }
}
