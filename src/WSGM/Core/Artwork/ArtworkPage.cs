using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One provider page, including whether its raw answer has another page.</summary>
/// <param name="Candidates">Filtered candidates from this page.</param>
/// <param name="HasMore">
///     Whether the provider's raw answer indicates another page, even if local filters removed every
///     candidate.
/// </param>
internal sealed record ArtworkPage(IReadOnlyList<ArtworkCandidate> Candidates, bool HasMore);
