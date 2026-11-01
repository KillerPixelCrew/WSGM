namespace WSGM.Core;

/// <summary>A game match from a SteamGridDB title search.</summary>
/// <param name="Id">SteamGridDB game id.</param>
/// <param name="Name">Game name.</param>
internal sealed record SgdbGame(int Id, string Name);
