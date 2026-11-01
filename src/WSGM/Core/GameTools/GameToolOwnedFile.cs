using System;

namespace WSGM.Core;

/// <summary>A file WSGM wrote into a game folder.</summary>
internal sealed record GameToolOwnedFile
{
    /// <summary>The path relative to the target directory, with forward slashes.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The SHA-256 of what WSGM wrote.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>The size WSGM wrote.</summary>
    public long Size { get; init; }

    /// <summary>The last write time after WSGM wrote it, for the quick revalidation.</summary>
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Binary or configuration.</summary>
    public GameToolFileKind Kind { get; init; }
}
