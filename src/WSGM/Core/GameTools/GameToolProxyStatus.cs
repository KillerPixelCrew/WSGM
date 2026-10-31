namespace WSGM.Core;

/// <summary>Availability of one proxy name in the target directory.</summary>
/// <param name="Name">The proxy file name.</param>
/// <param name="Hint">When to choose it.</param>
/// <param name="Available">Whether installing under this name is possible now.</param>
/// <param name="Conflict">Why not, or null.</param>
/// <param name="Suggest">Whether WSGM may pick it on its own.</param>
internal sealed record GameToolProxyStatus(string Name, string Hint, bool Available, string? Conflict, bool Suggest);
