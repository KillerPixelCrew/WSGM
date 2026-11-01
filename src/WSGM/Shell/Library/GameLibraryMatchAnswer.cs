namespace WSGM.Shell;

/// <summary>One game an artwork provider matched a search to.</summary>
/// <param name="Provider">The provider's id.</param>
/// <param name="ProviderName">The provider's name, as shown.</param>
/// <param name="Id">Its id for the game.</param>
/// <param name="Name">The game's name.</param>
/// <param name="Exact">Whether the provider calls it an exact match.</param>
public sealed record GameLibraryMatchAnswer(string Provider, string ProviderName, string Id, string Name, bool Exact);
