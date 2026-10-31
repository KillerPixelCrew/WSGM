namespace WSGM.Core;

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
