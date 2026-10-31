namespace WSGM.Core;

/// <summary>A game a provider matched a title to.</summary>
/// <param name="ProviderId">Stable id of the provider that matched it.</param>
/// <param name="Id">The provider's own game id.</param>
/// <param name="Name">The matched name.</param>
/// <param name="Exact">Whether the provider reports this as an exact rather than fuzzy match.</param>
internal sealed record ArtworkGameMatch(string ProviderId, string Id, string Name, bool Exact);
