namespace WSGM.Core;

/// <summary>How a tool's own binaries identify themselves regardless of their file name.</summary>
internal sealed record GameToolIdentity
{
    /// <summary>The version resource OriginalFilename every copy of the tool carries.</summary>
    public string OriginalFilename { get; init; } = "";

    /// <summary>Exact product names for tools whose filename varies by architecture or API.</summary>
    public string[] ProductNames { get; init; } = [];

    /// <summary>Export groups required in addition to product metadata; any complete group is sufficient.</summary>
    public string[][] ExportGroups { get; init; } = [];

    /// <summary>Tool-specific export groups sufficient even when version resources were stripped.</summary>
    public string[][] UniqueExportGroups { get; init; } = [];

    /// <summary>Whether one binary implements multiple proxy roles and its load name selects the role.</summary>
    public bool UniversalProxy { get; init; }
    /// <summary>False when the resource carries an emulated Windows DLL version rather than the tool release.</summary>
    public bool ResourceVersion { get; init; } = true;

    /// <summary>Tokens that make a file another product even if its OriginalFilename matches.</summary>
    public string[] VetoTokens { get; init; } = [];

    /// <summary>Other mods recognised by a token in their version resource, for conflict messages.</summary>
    public GameToolOtherTool[] OtherTools { get; init; } = [];
}
