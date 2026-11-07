namespace WSGM.Shell;

/// <summary>One proxy file name.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Hint">When to choose it.</param>
/// <param name="Available">Whether it can be used now.</param>
/// <param name="Conflict">Why not, or null.</param>
internal sealed record GameToolsProxyOption(string Name, string Hint, bool Available, string? Conflict)
{
    /// <summary>The supported API label, separate from guidance.</summary>
    public string Api { get; init; } = "";

    /// <summary>The exact game-local file set for this choice.</summary>
    public string[] Files { get; init; } = [];

    /// <summary>A dependency included with this choice, without a separate workflow.</summary>
    public string Dependency { get; init; } = "";
}
