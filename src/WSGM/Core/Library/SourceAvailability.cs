using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Whether a source's launcher is on this machine.</summary>
/// <param name="Installed">Whether it was found.</param>
/// <param name="Detail">What was found or why not, in one short phrase the sidebar shows.</param>
public sealed record SourceAvailability(bool Installed, string Detail)
{
    /// <summary>
    ///     For a ROM library that detects its system folders, the ones it holds, which the sidebar lists under it.
    ///     Empty for every other source.
    /// </summary>
    public IReadOnlyList<RomSystemFolder> Systems { get; init; } = [];

    /// <summary>A launcher that is not installed.</summary>
    public static SourceAvailability NotFound { get; } = new(false, "Not found");
}
