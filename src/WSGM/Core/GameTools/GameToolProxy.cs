namespace WSGM.Core;

/// <summary>Proxy names the tool installs under and recognises.</summary>
internal sealed record GameToolProxy
{
    /// <summary>The preferred install name.</summary>
    public string Default { get; init; } = "";

    /// <summary>Names offered for installation, in order.</summary>
    public GameToolProxyOption[] Options { get; init; } = [];

    /// <summary>Exact names probed for an existing copy, including names never offered.</summary>
    public string[] DetectionNames { get; init; } = [];

    /// <summary>Relative patterns whose identity-matched files also count as existing copies.</summary>
    public string[] DetectionPatterns { get; init; } = [];

    /// <summary>Introduces the file name choice before installation.</summary>
    public string Intro { get; init; } = "";

    /// <summary>Introduces the file name choice for an installed copy, which WSGM renames.</summary>
    public string RenameIntro { get; init; } = "";
}
