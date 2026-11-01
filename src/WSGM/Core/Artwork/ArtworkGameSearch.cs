using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>The games a title search found, and what each provider said about it.</summary>
/// <param name="Matches">Every provider's matches together, exact ones first.</param>
/// <param name="Outcomes">One entry per provider, in declaration order, including the ones not asked.</param>
/// <remarks>
///     An empty list has several causes a person needs told apart: a provider that needs a key, one
///     switched off, one that did not answer, and one that answered and knows no such game. Each
///     provider's own reason travels with the matches so the page can say which applies.
/// </remarks>
internal sealed record ArtworkGameSearch(
    IReadOnlyList<ArtworkGameMatch> Matches,
    IReadOnlyList<ArtworkProviderOutcome> Outcomes)
{
    /// <summary>What a provider that found nothing says when it answered.</summary>
    internal const string NoMatch = "No game by that name.";

    /// <summary>An empty search: nothing asked, nothing found.</summary>
    internal static ArtworkGameSearch Empty { get; } = new([], []);

    /// <summary>One line per provider that contributed no match, as "Provider: reason".</summary>
    /// <remarks>The same "name: reason" form the artwork page's provider notices use.</remarks>
    public IReadOnlyList<string> Reasons =>
    [
        .. Outcomes.Select(outcome => (outcome, reason: ReasonOf(outcome)))
            .Where(pair => pair.reason is not null)
            .Select(pair => $"{pair.outcome.ProviderName}: {pair.reason}")
    ];

    /// <summary>How many matches one provider contributed.</summary>
    /// <param name="providerId">The provider's id.</param>
    /// <returns>The count, zero for a provider that was not asked or found nothing.</returns>
    public int CountFrom(string providerId)
    {
        return Matches.Count(match => match.ProviderId == providerId);
    }

    /// <summary>Why one provider contributed nothing, or null when it contributed a match.</summary>
    /// <param name="outcome">The provider's outcome in this search.</param>
    /// <returns>Its setup reason, its failure, <see cref="NoMatch" />, or null.</returns>
    public string? ReasonOf(ArtworkProviderOutcome outcome)
    {
        if (!outcome.Status.IsReady)
        {
            return outcome.Status.Detail.Length > 0 ? outcome.Status.Detail : null;
        }

        return outcome.Failure ?? (CountFrom(outcome.ProviderId) == 0 ? NoMatch : null);
    }
}
