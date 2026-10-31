namespace WSGM.Core;

/// <summary>A game-shipped file WSGM backed up before replacing it.</summary>
internal sealed record GameToolReplacedFile
{
    /// <summary>The path relative to the target directory, with forward slashes.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The SHA-256 of the original.</summary>
    public string OriginalSha256 { get; init; } = "";

    /// <summary>The absolute backup path under WSGM's data folder.</summary>
    public string BackupPath { get; init; } = "";
}
