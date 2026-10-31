using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Everything the Graphics destination and the Graphics page in Steam draw.</summary>
/// <param name="Publishers">The running graphics publishers.</param>
/// <param name="Sections">Their sections, publisher by publisher, each in declared order.</param>
internal sealed record GraphicsOverlaySnapshot(
    IReadOnlyList<GraphicsOverlayPublisher> Publishers,
    IReadOnlyList<GraphicsOverlaySection> Sections)
{
    /// <summary>No graphics publisher is running.</summary>
    internal static GraphicsOverlaySnapshot Empty { get; } = new([], []);

    /// <summary>Whether the Graphics surfaces are offered: at least one graphics publisher runs.</summary>
    internal bool Visible => Publishers.Count > 0;
}
