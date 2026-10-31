namespace WSGM.Core;

/// <summary>What one provider contributed to a search, including the reason it contributed nothing.</summary>
/// <param name="ProviderName">The provider's display name.</param>
/// <param name="Status">Whether it was searched at all.</param>
/// <param name="Failure">The user-facing failure, or null when the provider answered.</param>
/// <param name="ProviderId">The provider that answered or failed.</param>
internal sealed record ArtworkProviderOutcome(
    string ProviderName,
    ArtworkProviderStatus Status,
    string? Failure,
    string ProviderId = "");
