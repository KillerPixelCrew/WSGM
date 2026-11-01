namespace WSGM.Core;

/// <summary>A title search with optional provider system and backing-file identity.</summary>
internal sealed record ArtworkGameQuery(string Name, int SystemId = 0, string RomName = "", long RomSize = 0);
