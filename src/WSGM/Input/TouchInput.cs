using System;

namespace WSGM.Input;

/// <summary>Timing shared by surfaces that close in response to touch.</summary>
internal static class TouchInput
{
    /// <summary>How long a surface stays open after a touch asks it to close.</summary>
    /// <remarks>
    /// Avalonia promotes the touch to a synthesized click that arrives after the handler ran. Closing
    /// only after this grace lets the window eat that click instead of whatever sits beneath it; see
    /// the touch-promotion finding in <c>docs\overlay-and-input.md</c>.
    /// </remarks>
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromMilliseconds(150);
}
