namespace WSGM.Core;

/// <summary>Controls the raw-input edge-swipe activation areas.</summary>
/// <remarks>
///     Top opens WSGM's quick access sheet. Bottom is disabled by default; enabling it opens
///     the Open apps strip in game mode. Explorer retains that edge in desktop mode.
///     Left and right send Steam Big Picture's own menu shortcuts.
/// </remarks>
public sealed class GestureConfig
{
    /// <summary>
    ///     Whether a swipe up from the bottom edge opens the quick access sheet
    ///     with focus on its Open apps strip (game mode only).
    /// </summary>
    public bool BottomEdge { get; set; }

    /// <summary>Whether a swipe down from the top edge opens the quick access sheet.</summary>
    public bool TopEdge { get; set; } = true;

    /// <summary>Whether a swipe from the left edge opens Steam's Big Picture menu.</summary>
    public bool LeftEdgeSteamMenu { get; set; } = true;

    /// <summary>Whether a swipe from the right edge opens Steam's Big Picture quick-access menu.</summary>
    public bool RightEdgeSteamQuickAccess { get; set; } = true;
}
