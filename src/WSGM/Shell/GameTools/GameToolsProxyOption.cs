namespace WSGM.Shell;

/// <summary>One proxy file name.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Hint">When to choose it.</param>
/// <param name="Available">Whether it can be used now.</param>
/// <param name="Conflict">Why not, or null.</param>
internal sealed record GameToolsProxyOption(string Name, string Hint, bool Available, string? Conflict);
