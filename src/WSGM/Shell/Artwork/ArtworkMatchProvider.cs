using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What one artwork provider said to a game search: the Game Library's "Fix match" or the artwork page's.</summary>
/// <param name="Provider">The provider's id.</param>
/// <param name="Name">The provider's name, as shown.</param>
/// <param name="Status">
///     <c>found</c>, <c>none</c> (asked, knows no such game), <c>failed</c> (could not be asked or did not
///     answer), <c>setup</c> (needs credentials) or <c>off</c> (turned off in Settings).
/// </param>
/// <param name="Detail">Why it found nothing, in the provider's own words; empty when it found games.</param>
/// <param name="Count">How many games it matched.</param>
public sealed record ArtworkMatchProvider(string Provider, string Name, string Status, string Detail, int Count)
{
    /// <summary>Every provider's outcome in a finished search, in declaration order.</summary>
    /// <param name="search">The finished search.</param>
    /// <returns>One entry per provider, asked or not.</returns>
    internal static IReadOnlyList<ArtworkMatchProvider> Of(ArtworkGameSearch search)
    {
        return
        [
            .. search.Outcomes.Select(outcome =>
            {
                var count = search.CountFrom(outcome.ProviderId);
                var status = outcome.Status.Readiness switch
                {
                    ArtworkProviderReadiness.MissingCredentials => "setup",
                    ArtworkProviderReadiness.Disabled => "off",
                    _ when outcome.Failure is not null => "failed",
                    _ => count > 0 ? "found" : "none"
                };
                return new ArtworkMatchProvider(
                    outcome.ProviderId, outcome.ProviderName, status, search.ReasonOf(outcome) ?? string.Empty,
                    count);
            })
        ];
    }
}
