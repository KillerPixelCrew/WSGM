using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>
/// The shared card-row button used by WSGM's quick access panel and other
/// list-style surfaces: a vector icon, a title with an optional description
/// caption, and optional trailing text (for example a controller glyph hint).
/// It derives from <see cref="Button"/> so Click handlers, x:Name references,
/// tab order, and gamepad navigation keep working unchanged; the visuals live
/// in the CardButton ControlTheme (Themes\CardButtonTheme.axaml) and carry the
/// established quick-row look, including the border-based focus visual and the
/// <c>primary</c>/<c>danger</c> style classes.
/// </summary>
public class CardButton : Button
{
    /// <summary>
    /// Defines the <see cref="IconGeometry"/> property.
    /// </summary>
    public static readonly StyledProperty<Geometry?> IconGeometryProperty =
        AvaloniaProperty.Register<CardButton, Geometry?>(nameof(IconGeometry));

    /// <summary>
    /// Defines the <see cref="Title"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(Title));

    /// <summary>
    /// Defines the <see cref="Description"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(Description));

    /// <summary>
    /// Defines the <see cref="TrailingText"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TrailingTextProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(TrailingText));

    /// <summary>
    /// Defines the <see cref="StatusBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush?> StatusBrushProperty =
        AvaloniaProperty.Register<CardButton, IBrush?>(nameof(StatusBrush));

    /// <summary>
    /// Gets or sets the vector icon rendered at the left edge of the card,
    /// stroked with the button's foreground brush. The icon slot collapses
    /// when this is null.
    /// </summary>
    public Geometry? IconGeometry
    {
        get => GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    /// <summary>
    /// Gets or sets the primary label of the card (the setting-title line).
    /// Two-step confirmation flows may rewrite this at runtime, exactly like
    /// the previous per-row title TextBlocks.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the caption line rendered under the title. The caption
    /// collapses when this is null or empty.
    /// </summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// Gets or sets the semibold trailing text at the right edge of the card
    /// (for example the "A" activation hint). The trailing slot collapses when
    /// this is null or empty.
    /// </summary>
    public string? TrailingText
    {
        get => GetValue(TrailingTextProperty);
        set => SetValue(TrailingTextProperty, value);
    }

    /// <summary>
    /// Gets or sets the fill of the small status dot rendered before the trailing
    /// text (a state indicator, for example the wake-lock colors). The dot slot
    /// collapses when this is null.
    /// </summary>
    public IBrush? StatusBrush
    {
        get => GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }
}
