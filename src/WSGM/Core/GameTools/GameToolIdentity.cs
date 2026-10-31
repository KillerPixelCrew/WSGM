namespace WSGM.Core;

/// <summary>How a tool's own binaries identify themselves regardless of their file name.</summary>
internal sealed record GameToolIdentity
{
    /// <summary>The version resource OriginalFilename every copy of the tool carries.</summary>
    public string OriginalFilename { get; init; } = "";

    /// <summary>Tokens that make a file another product even if its OriginalFilename matches.</summary>
    public string[] VetoTokens { get; init; } = [];

    /// <summary>Other mods recognised by a token in their version resource, for conflict messages.</summary>
    public GameToolOtherTool[] OtherTools { get; init; } = [];
}
