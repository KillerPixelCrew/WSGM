using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

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
