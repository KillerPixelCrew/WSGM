namespace WSGM.Core;

/// <summary>A name the primary file may be installed as.</summary>
internal sealed record GameToolProxyOption
{
    /// <summary>The file name in the target directory.</summary>
    public string Name { get; init; } = "";

    /// <summary>A short explanation of when to choose it.</summary>
    public string Hint { get; init; } = "";

    /// <summary>The supported graphics APIs, empty for an ordinary proxy alias.</summary>
    public string Api { get; init; } = "";

    /// <summary>Whether WSGM may pick it on its own; false for names that need something else installed first.</summary>
    public bool Suggest { get; init; } = true;
}
