using System;
using WSGM.Core;
using WSGM.Themes;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    // --- Appearance: accent color ---

    /// <summary>
    ///     Gets or sets the UI accent color as a hex string (e.g. "#FF9D3D").
    ///     An unparsable value falls back to the default accent when applied.
    /// </summary>
    public string AccentColorHex
    {
        get;
        set => SetField(ref field, value, nameof(AccentColorHex));
    } = AccentPalette.DefaultAccent;

    // --- Appearance: boot splash ---
    // The editor binds the SplashConfig instance directly ({Binding Splash.X}).
    // Only members with a dependent consumer keep an INPC wrapper here: the four
    // colors repaint their swatch previews on every keystroke, and the two image
    // paths drive the Appearance page's thumbnail refresh.

    /// <summary>
    ///     The splash section being edited. Replaced wholesale by
    ///     <see cref="LoadSplash" /> (startup, preset apply, theme import), which raises
    ///     this property so every nested binding re-evaluates.
    /// </summary>
    public SplashConfig Splash { get; private set; } = new();

    /// <summary>Editable placement of the splash text stack.</summary>
    public SplashPlacementEditor TextPlacement { get; } = new();

    /// <summary>Editable placement of the splash spinner.</summary>
    public SplashPlacementEditor SpinnerPlacement { get; } = new();

    /// <summary>Editable placement of the splash logo.</summary>
    public SplashPlacementEditor LogoPlacement { get; } = new();

    /// <summary>Spinner styles offered by the settings selector.</summary>
    public static SplashSpinnerStyle[] SpinnerStyleValues { get; } = Enum.GetValues<SplashSpinnerStyle>();

    /// <summary>Sweep-line edges offered by the settings selector.</summary>
    // ReSharper disable once CollectionNeverQueried.Global
    public static SweepEdge[] SweepEdgeValues { get; } = Enum.GetValues<SweepEdge>();

    /// <summary>Placement modes offered for the spinner and logo.</summary>
    public static SplashPlacementMode[] PlacementModeValues { get; } = Enum.GetValues<SplashPlacementMode>();

    /// <summary>
    ///     Placement modes offered for the text element itself, which cannot
    ///     ride its own stack.
    /// </summary>
    public static SplashPlacementMode[] TextPlacementModeValues { get; } =
        [SplashPlacementMode.Anchor, SplashPlacementMode.Absolute];

    /// <summary>Nine-grid anchors offered by the settings selectors.</summary>
    public static SplashPlacementAnchor[] PlacementAnchorValues { get; } = Enum.GetValues<SplashPlacementAnchor>();

    /// <summary>Gets or sets the splash title color as a hex string.</summary>
    public string SplashTextColorHex
    {
        get => Splash.TextColor;
        set
        {
            Splash.TextColor = value;
            Raise(nameof(SplashTextColorHex));
        }
    }

    /// <summary>Gets or sets the splash caption color as a hex string.</summary>
    public string SplashCaptionColorHex
    {
        get => Splash.CaptionColor;
        set
        {
            Splash.CaptionColor = value;
            Raise(nameof(SplashCaptionColorHex));
        }
    }

    /// <summary>Gets or sets the spinner color as a hex string.</summary>
    public string SplashSpinnerColorHex
    {
        get => Splash.SpinnerColor;
        set
        {
            Splash.SpinnerColor = value;
            Raise(nameof(SplashSpinnerColorHex));
        }
    }

    /// <summary>Gets or sets the splash background fill color as a hex string.</summary>
    public string SplashBackgroundColorHex
    {
        get => Splash.BackgroundColor;
        set
        {
            Splash.BackgroundColor = value;
            Raise(nameof(SplashBackgroundColorHex));
        }
    }

    /// <summary>Gets or sets the splash logo image path; empty = no logo.</summary>
    public string SplashLogoPath
    {
        get => Splash.LogoImagePath;
        set
        {
            Splash.LogoImagePath = value;
            Raise(nameof(SplashLogoPath));
        }
    }

    /// <summary>Gets or sets the splash background image path; empty = solid color.</summary>
    public string SplashBackgroundImagePath
    {
        get => Splash.BackgroundImagePath;
        set
        {
            Splash.BackgroundImagePath = value;
            Raise(nameof(SplashBackgroundImagePath));
        }
    }

    /// <summary>
    ///     Builds the splash section handed to Save, the preview window, and
    ///     theme export: an isolated copy of the edited section, so the save path's
    ///     asset staging can rewrite its image paths without touching the editor.
    /// </summary>
    internal SplashConfig BuildSplashConfig()
    {
        var splash = ConfigStore.CloneJson(Splash, ConfigJsonContext.Default.SplashConfig);
        // "With text" is a spinner/logo-only mode; the text element itself anchors.
        // Normalize accepts WithText on every placement, so an imported theme can
        // still carry it on the text placement — this is where it is coerced.
        if (splash.TextPlacement.Mode == SplashPlacementMode.WithText)
        {
            splash.TextPlacement.Mode = SplashPlacementMode.Anchor;
        }

        return splash;
    }

    /// <summary>
    ///     Loads the splash editor from a splash section — used at startup, on
    ///     preset apply, and after theme import. The section is copied and normalized,
    ///     so later edits cannot mutate the caller's instance and an imported value can
    ///     never carry an out-of-range enum into the editor.
    /// </summary>
    internal void LoadSplash(SplashConfig splash)
    {
        Splash = ConfigStore.NormalizeSplash(
            ConfigStore.CloneJson(splash, ConfigJsonContext.Default.SplashConfig));
        TextPlacement.Load(Splash.TextPlacement);
        SpinnerPlacement.Load(Splash.SpinnerPlacement);
        LogoPlacement.Load(Splash.LogoPlacement);
        Raise(nameof(Splash));
        Raise(nameof(SplashTextColorHex));
        Raise(nameof(SplashCaptionColorHex));
        Raise(nameof(SplashSpinnerColorHex));
        Raise(nameof(SplashBackgroundColorHex));
        Raise(nameof(SplashLogoPath));
        Raise(nameof(SplashBackgroundImagePath));
    }
}
