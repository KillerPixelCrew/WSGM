using System;

namespace WSGM.Core;

/// <summary>The remembered runtime executable of a game.</summary>
internal sealed record GameRuntimeChoice
{
    /// <summary>The executable relative to the install root, or absolute when the user picked a file outside it.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>The size when confirmed.</summary>
    public long Size { get; init; }

    /// <summary>The last write time when confirmed.</summary>
    public DateTime LastWriteUtc { get; init; }
}
