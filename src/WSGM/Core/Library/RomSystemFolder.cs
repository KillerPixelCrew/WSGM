namespace WSGM.Core;

/// <summary>One system folder a ROM library holds, as detecting its system folders finds it.</summary>
/// <param name="SystemId">The canonical system the folder's name names.</param>
/// <param name="Name">The system's name.</param>
/// <param name="Folder">The folder.</param>
public sealed record RomSystemFolder(string SystemId, string Name, string Folder);
