namespace WSGM.Core;

/// <summary>Nine-grid anchor positions for a boot-splash element.</summary>
public enum SplashPlacementAnchor
{
    /// <summary>Top-left corner of the screen.</summary>
    TopLeft,

    /// <summary>Top edge, horizontally centered.</summary>
    TopCenter,

    /// <summary>Top-right corner of the screen.</summary>
    TopRight,

    /// <summary>Left edge, vertically centered.</summary>
    CenterLeft,

    /// <summary>Center of the screen.</summary>
    Center,

    /// <summary>Right edge, vertically centered.</summary>
    CenterRight,

    /// <summary>Bottom-left corner of the screen.</summary>
    BottomLeft,

    /// <summary>Bottom edge, horizontally centered.</summary>
    BottomCenter,

    /// <summary>Bottom-right corner of the screen.</summary>
    BottomRight
}

/// <summary>How a boot-splash element is positioned on screen.</summary>
public enum SplashPlacementMode
{
    /// <summary>
    ///     Positioned by a nine-grid anchor plus edge padding — the portable
    ///     option that adapts to any screen size.
    /// </summary>
    Anchor,

    /// <summary>
    ///     Positioned at absolute logical pixel coordinates (device-specific;
    ///     anchors are the portable option).
    /// </summary>
    Absolute,

    /// <summary>
    ///     Rendered inside the text stack (spinner/logo only), following the
    ///     text element wherever it is placed.
    /// </summary>
    WithText
}

/// <summary>Visual style of the boot-splash progress spinner.</summary>
public enum SplashSpinnerStyle
{
    /// <summary>The classic in-repo rotating arc ring.</summary>
    Ring,

    /// <summary>LoadingIndicators.Avalonia "Arc" mode.</summary>
    LiArc,

    /// <summary>LoadingIndicators.Avalonia "Arcs" mode.</summary>
    LiArcs,

    /// <summary>LoadingIndicators.Avalonia "ArcsRing" mode.</summary>
    LiArcsRing,

    /// <summary>LoadingIndicators.Avalonia "DoubleBounce" mode.</summary>
    LiDoubleBounce,

    /// <summary>LoadingIndicators.Avalonia "FlipPlane" mode.</summary>
    LiFlipPlane,

    /// <summary>LoadingIndicators.Avalonia "Pulse" mode.</summary>
    LiPulse,

    /// <summary>LoadingIndicators.Avalonia "Ring" mode.</summary>
    LiRing,

    /// <summary>LoadingIndicators.Avalonia "ThreeDots" mode.</summary>
    LiThreeDots,

    /// <summary>LoadingIndicators.Avalonia "Wave" mode.</summary>
    LiWave,

    /// <summary>
    ///     In-repo sweeping line along a screen edge (see
    ///     <see cref="SplashConfig.SweepEdge" />).
    /// </summary>
    SweepLine,

    /// <summary>No spinner at all (no animation timer is created).</summary>
    Off
}

/// <summary>Which screen edge the sweep-line spinner travels along.</summary>
public enum SweepEdge
{
    /// <summary>Sweep along the bottom edge of the screen.</summary>
    Bottom,

    /// <summary>Sweep along the top edge of the screen.</summary>
    Top
}

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

/// <summary>
///     Boot-splash customization: text, spinner, background, logo, and per-element
///     placement. Defaults reproduce the classic look (black background, white "Please
///     wait" with a ring spinner, centered). Colors are <c>#RRGGBB</c> strings parsed
///     with a logged fallback, so a bad value can never break the boot cover.
/// </summary>
public sealed class SplashConfig
{
    /// <summary>Title text shown on the splash.</summary>
    public string Text { get; set; } = "Please wait";

    /// <summary>Whether the text block (title + caption) is rendered at all.</summary>
    public bool TextEnabled { get; set; } = true;

    /// <summary>Title text color as a <c>#RRGGBB</c> string.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Title font size in logical pixels.</summary>
    public int TitleFontSize { get; set; } = 26;

    /// <summary>Optional caption line rendered under the title; empty = no caption.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Caption text color as a <c>#RRGGBB</c> string.</summary>
    public string CaptionColor { get; set; } = "#666666";

    /// <summary>Caption font size in logical pixels.</summary>
    public int CaptionFontSize { get; set; } = 12;

    /// <summary>
    ///     Spinner style; <see cref="SplashSpinnerStyle.Off" /> disables the
    ///     spinner and its animation timer.
    /// </summary>
    public SplashSpinnerStyle SpinnerStyle { get; set; } = SplashSpinnerStyle.Ring;

    /// <summary>Spinner color as a <c>#RRGGBB</c> string.</summary>
    public string SpinnerColor { get; set; } = "#FFFFFF";

    /// <summary>Spinner size (diameter/height) in logical pixels.</summary>
    public int SpinnerSize { get; set; } = 36;

    /// <summary>Edge the <see cref="SplashSpinnerStyle.SweepLine" /> spinner travels along.</summary>
    public SweepEdge SweepEdge { get; set; } = SweepEdge.Bottom;

    /// <summary>Background fill color as a <c>#RRGGBB</c> string.</summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>Whether a radial vignette overlay darkens the background edges.</summary>
    public bool VignetteEnabled { get; set; }

    /// <summary>
    ///     Full-screen background image path; empty = solid color only. A
    ///     missing or unreadable file falls back to the color with a logged warning.
    /// </summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>
    ///     Logo image path; empty = no logo. A missing or unreadable file is
    ///     skipped with a logged warning.
    /// </summary>
    public string LogoImagePath { get; set; } = "";

    /// <summary>Maximum logo edge length in logical pixels (aspect ratio preserved).</summary>
    public int LogoMaxSize { get; set; } = 200;

    /// <summary>Placement of the text stack (title + caption).</summary>
    public SplashElementPlacement TextPlacement { get; set; } = new();

    /// <summary>Placement of the spinner; defaults to riding inside the text stack.</summary>
    public SplashElementPlacement SpinnerPlacement { get; set; } = new() { Mode = SplashPlacementMode.WithText };

    /// <summary>Placement of the logo; defaults to riding inside the text stack.</summary>
    public SplashElementPlacement LogoPlacement { get; set; } = new() { Mode = SplashPlacementMode.WithText };
}
