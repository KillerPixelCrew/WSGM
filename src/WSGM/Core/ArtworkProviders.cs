using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Why a provider cannot be searched right now.</summary>
/// <remarks>
/// Separate from "no results" on purpose. A provider that needs credentials nobody has entered
/// returns nothing, and so does a provider that searched and found nothing; showing both as an
/// empty grid tells the user their game has no artwork when the truth is that a source was never
/// asked. That distinction is the whole reason this is a first-class state.
/// </remarks>
public enum ArtworkProviderReadiness
{
    /// <summary>Configured and usable.</summary>
    Ready,

    /// <summary>Turned off by the user.</summary>
    Disabled,

    /// <summary>Needs credentials that are not configured.</summary>
    MissingCredentials,
}

/// <summary>Whether a provider can be searched, and what to say when it cannot.</summary>
/// <param name="Readiness">The provider's current state.</param>
/// <param name="Detail">A user-facing sentence when it is not <see cref="ArtworkProviderReadiness.Ready"/>.</param>
public readonly record struct ArtworkProviderStatus(ArtworkProviderReadiness Readiness, string Detail = "")
{
    /// <summary>Whether the provider may be searched.</summary>
    public bool IsReady => Readiness == ArtworkProviderReadiness.Ready;

    /// <summary>A ready status.</summary>
    public static ArtworkProviderStatus Ready { get; } = new(ArtworkProviderReadiness.Ready);
}

/// <summary>One artwork candidate, tagged with the source that supplied it.</summary>
/// <param name="ProviderId">Stable id of the provider, for attribution and de-duplication.</param>
/// <param name="ProviderName">The provider's display name, shown as the source.</param>
/// <param name="Id">The provider's own asset id, or zero when it has none.</param>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL for the picker grid.</param>
/// <param name="Width">Pixel width, or zero when the provider does not report one.</param>
/// <param name="Height">Pixel height, or zero when the provider does not report one.</param>
/// <param name="Extension">Verified static image format, <c>png</c> or <c>jpg</c>.</param>
public sealed record ArtworkCandidate(
    string ProviderId,
    string ProviderName,
    int Id,
    string Url,
    string Thumb,
    int Width,
    int Height,
    string Extension);

/// <summary>A game a provider matched a title to.</summary>
/// <param name="ProviderId">Stable id of the provider that matched it.</param>
/// <param name="Id">The provider's own game id.</param>
/// <param name="Name">The matched name.</param>
/// <param name="Exact">Whether the provider reports this as an exact rather than fuzzy match.</param>
public sealed record ArtworkGameMatch(string ProviderId, string Id, string Name, bool Exact);

/// <summary>One artwork source.</summary>
/// <remarks>
/// Everything source-specific lives behind this: endpoints, authentication, media vocabularies,
/// rate limits and their error shapes. What comes back out is the same for every provider, which is
/// what keeps the picker free of provider knowledge. Applying a chosen image is deliberately not
/// here — that is one Steam client call and is the same whichever source supplied the bytes.
/// </remarks>
public interface IArtworkProvider
{
    /// <summary>Stable identifier, used in attribution and configuration.</summary>
    string Id { get; }

    /// <summary>The name shown to the user as the source of a result.</summary>
    string DisplayName { get; }

    /// <summary>Whether this provider can be searched with the current configuration.</summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>The provider's readiness and, when it is not ready, why.</returns>
    ArtworkProviderStatus GetStatus(AppConfig config);

    /// <summary>Searches the provider for games matching a title.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="config">The loaded configuration, for credentials.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matches, best first.</returns>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, AppConfig config, CancellationToken cancellationToken);

    /// <summary>Lists artwork for a game this provider matched.</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="gameId">A game id this provider returned from <see cref="SearchGamesAsync"/>.</param>
    /// <param name="config">The loaded configuration, for credentials.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The candidates, best first.</returns>
    Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, AppConfig config, CancellationToken cancellationToken);

    /// <summary>Lists artwork for a Steam app id, when the provider can address one directly.</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="steamAppId">The Steam app id.</param>
    /// <param name="config">The loaded configuration, for credentials.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The candidates, or an empty list when this provider cannot address Steam ids.</returns>
    Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, AppConfig config, CancellationToken cancellationToken);
}

/// <summary>What one provider contributed to a search, including the reason it contributed nothing.</summary>
/// <param name="ProviderId">The provider's stable id.</param>
/// <param name="ProviderName">The provider's display name.</param>
/// <param name="Status">Whether it was searched at all.</param>
/// <param name="Failure">The user-facing failure, or null when the provider answered.</param>
/// <param name="Count">How many candidates it contributed after de-duplication.</param>
public sealed record ArtworkProviderOutcome(
    string ProviderId,
    string ProviderName,
    ArtworkProviderStatus Status,
    string? Failure,
    int Count);

/// <summary>The merged result of asking every provider.</summary>
/// <param name="Candidates">Every candidate, ranked.</param>
/// <param name="Outcomes">One entry per provider, in declaration order.</param>
public sealed record ArtworkSearchResult(
    IReadOnlyList<ArtworkCandidate> Candidates,
    IReadOnlyList<ArtworkProviderOutcome> Outcomes)
{
    /// <summary>Whether every ready provider failed, which is different from finding nothing.</summary>
    /// <remarks>
    /// An empty grid needs a reason. If no provider was even ready, or all of them faulted, the
    /// picker must say so rather than report that the game has no artwork.
    /// </remarks>
    public bool NoProviderAnswered => !Outcomes.Any(o => o.Status.IsReady && o.Failure is null);

    /// <summary>The failures worth showing, one per provider that was asked and could not answer.</summary>
    public IReadOnlyList<string> Failures =>
        Outcomes.Where(o => o.Failure is not null).Select(o => $"{o.ProviderName}: {o.Failure}").ToArray();

    /// <summary>The providers that were skipped, with the reason each was skipped.</summary>
    public IReadOnlyList<string> Skipped =>
        Outcomes.Where(o => !o.Status.IsReady && o.Status.Detail.Length > 0)
            .Select(o => $"{o.ProviderName}: {o.Status.Detail}").ToArray();
}

/// <summary>Asks every configured artwork provider and merges what comes back.</summary>
/// <remarks>
/// Providers are searched in parallel rather than in priority order. Fallback would mean a slow or
/// empty primary hides a good secondary result, and the acceptance this was written against is that
/// one provider's failure does not break another — which only holds if the others were actually
/// asked. Ranking then restores the intent that the primary source leads.
/// </remarks>
public static class ArtworkSearch
{
    /// <summary>The providers, in the order their results are preferred on a tie.</summary>
    public static IReadOnlyList<IArtworkProvider> Providers { get; } =
        [new SteamGridDbProvider(), new ScreenscraperProvider()];

    /// <summary>Finds the provider with this id, or null.</summary>
    /// <param name="providerId">A provider id previously returned in a result.</param>
    /// <returns>The provider, or null when no provider claims that id.</returns>
    public static IArtworkProvider? Find(string providerId) =>
        Providers.FirstOrDefault(p => p.Id == providerId);

    /// <summary>Searches every ready provider for games matching a title.</summary>
    /// <param name="term">The search term.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Matches from every provider that answered, exact matches first.</returns>
    public static async Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var ready = Providers.Where(p => p.GetStatus(config).IsReady).ToArray();
        var results = await Task.WhenAll(ready.Select(async provider =>
        {
            try
            {
                return await provider.SearchGamesAsync(term, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Artwork provider {provider.Id} title search failed: {ex.Message}");
                return (IReadOnlyList<ArtworkGameMatch>)[];
            }
        })).ConfigureAwait(false);

        // Exact matches first, then provider declaration order, then the provider's own ranking.
        return results
            .SelectMany((matches, index) => matches.Select(match => (match, index)))
            .OrderByDescending(pair => pair.match.Exact)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.match)
            .ToArray();
    }

    /// <summary>Searches every ready provider for artwork for a Steam app.</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="steamAppId">The Steam app id.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The merged, de-duplicated and ranked candidates with per-provider outcomes.</returns>
    public static Task<ArtworkSearchResult> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, AppConfig config, CancellationToken cancellationToken = default)
        => GatherAsync(config, (provider, token) =>
            provider.GetAssetsForSteamAppAsync(asset, steamAppId, config, token), cancellationToken);

    /// <summary>Searches one provider for artwork for a game the user chose from its matches.</summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="match">A match previously returned by <see cref="SearchGamesAsync"/>.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The candidates and that provider's outcome.</returns>
    /// <remarks>
    /// One provider, not all of them: a game id belongs to the source that issued it, and asking
    /// another provider for it would either return nothing or, worse, return a different game's
    /// artwork that happened to share the number.
    /// </remarks>
    public static Task<ArtworkSearchResult> GetAssetsForMatchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        var provider = Find(match.ProviderId);
        return provider is null
            ? Task.FromResult(new ArtworkSearchResult([], []))
            : GatherAsync(config, (p, token) =>
                p.GetAssetsForGameAsync(asset, match.Id, config, token), cancellationToken, provider);
    }

    private static async Task<ArtworkSearchResult> GatherAsync(
        AppConfig config,
        Func<IArtworkProvider, CancellationToken, Task<IReadOnlyList<ArtworkCandidate>>> fetch,
        CancellationToken cancellationToken,
        IArtworkProvider? only = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var providers = only is null ? Providers : [only];
        var statuses = providers.Select(p => p.GetStatus(config)).ToArray();

        var answers = await Task.WhenAll(providers.Select(async (provider, index) =>
        {
            if (!statuses[index].IsReady)
            {
                return (Candidates: (IReadOnlyList<ArtworkCandidate>)[], Failure: (string?)null);
            }
            try
            {
                var candidates = await fetch(provider, cancellationToken).ConfigureAwait(false);
                return (Candidates: candidates, Failure: (string?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SteamGridDbException ex)
            {
                return (Candidates: (IReadOnlyList<ArtworkCandidate>)[], Failure: (string?)ex.Message);
            }
            catch (Exception ex)
            {
                Log.Warn($"Artwork provider {provider.Id} failed: {ex.Message}");
                return (
                    Candidates: (IReadOnlyList<ArtworkCandidate>)[],
                    Failure: (string?)$"{provider.DisplayName} could not be reached.");
            }
        })).ConfigureAwait(false);

        // De-duplicated by URL: the same asset genuinely does come back from more than one source,
        // and the picker showing it twice is the noise this issue asks to avoid. The earlier
        // provider wins, which is what makes the declaration order a preference rather than decoration.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ArtworkCandidate>();
        var counts = new int[providers.Count];
        for (int index = 0; index < providers.Count; index++)
        {
            foreach (var candidate in answers[index].Candidates)
            {
                if (seen.Add(candidate.Url))
                {
                    candidates.Add(candidate);
                    counts[index]++;
                }
            }
        }

        var outcomes = providers
            .Select((provider, index) => new ArtworkProviderOutcome(
                provider.Id, provider.DisplayName, statuses[index], answers[index].Failure, counts[index]))
            .ToArray();
        return new ArtworkSearchResult(candidates, outcomes);
    }
}
