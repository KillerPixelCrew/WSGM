namespace WSGM.Core;

/// <summary>Position of one boot-splash element (text, spinner, or logo).</summary>
public sealed class SplashElementPlacement
{
    /// <summary>
    ///     How this element is positioned. <see cref="SplashPlacementMode.WithText" />
    ///     is honored for the spinner and logo only.
    /// </summary>
    public SplashPlacementMode Mode { get; set; } = SplashPlacementMode.Anchor;

    /// <summary>Nine-grid anchor used in <see cref="SplashPlacementMode.Anchor" /> mode.</summary>
    public SplashPlacementAnchor Anchor { get; set; } = SplashPlacementAnchor.Center;

    /// <summary>
    ///     Horizontal padding in logical pixels from the anchored edge; ignored
    ///     on a horizontally centered axis.
    /// </summary>
    public int PaddingX { get; set; } = 64;

    /// <summary>
    ///     Vertical padding in logical pixels from the anchored edge; ignored
    ///     on a vertically centered axis.
    /// </summary>
    public int PaddingY { get; set; } = 64;

    /// <summary>
    ///     Absolute X coordinate in logical pixels for
    ///     <see cref="SplashPlacementMode.Absolute" /> mode (device-specific — anchors
    ///     are the portable option).
    /// </summary>
    public int X { get; set; }

    /// <summary>
    ///     Absolute Y coordinate in logical pixels for
    ///     <see cref="SplashPlacementMode.Absolute" /> mode (device-specific — anchors
    ///     are the portable option).
    /// </summary>
    public int Y { get; set; }
}
