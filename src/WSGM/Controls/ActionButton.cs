using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>A labelled command with optional explanation, stroke icon and activation hint.</summary>
/// <remarks>Use on the Avalonia UI thread. The owner supplies command behavior; this control owns its content layout.</remarks>
public class ActionButton : Button
{
    /// <summary>Identifies the command label.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ActionButton, string?>(nameof(Title));

    /// <summary>Identifies the optional command explanation.</summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<ActionButton, string?>(nameof(Description));

    /// <summary>Identifies the optional command icon.</summary>
    public static readonly StyledProperty<Geometry?> IconGeometryProperty =
        AvaloniaProperty.Register<ActionButton, Geometry?>(nameof(IconGeometry));

    /// <summary>Identifies the activation hint.</summary>
    public static readonly StyledProperty<string?> TrailingTextProperty =
        AvaloniaProperty.Register<ActionButton, string?>(nameof(TrailingText));

    /// <summary>Identifies the Quick Access pin marker.</summary>
    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<ActionButton, bool>(nameof(IsPinned));

    /// <summary>Identifies the physical-controller glyph that takes precedence over the trailing text.</summary>
    internal static readonly StyledProperty<PhysicalGlyphRenderPlan?> TrailingGlyphProperty =
        AvaloniaProperty.Register<ActionButton, PhysicalGlyphRenderPlan?>(nameof(TrailingGlyph));

    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private readonly PhysicalGlyphImage _glyph = new() { Width = 18, Height = 18 };
    private readonly TextBlock _hint = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly Path _icon = new()
    {
        Width = 20, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Path _pin = new()
    {
        Data = Icons.Pin, Width = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

    /// <summary>Creates a command using the ordinary button template and focus behavior.</summary>
    public ActionButton()
    {
        Classes.Add("deck-action");
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_title);
        labels.Children.Add(_description);
        var hints = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        hints.Children.Add(_pin);
        hints.Children.Add(_glyph);
        hints.Children.Add(_hint);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        layout.Children.Add(_icon);
        Grid.SetColumn(labels, 1);
        layout.Children.Add(labels);
        Grid.SetColumn(hints, 2);
        layout.Children.Add(hints);
        Content = layout;
        RefreshPresentation();
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Visible command label, also used as the automation name; null produces an empty name.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Supporting text; null or empty hides the description without reserving space.</summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Stroke geometry tinted with the foreground; null hides the icon.</summary>
    public Geometry? IconGeometry
    {
        get => GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    /// <summary>Trailing text shown when no physical glyph is supplied; null or empty hides it.</summary>
    public string? TrailingText
    {
        get => GetValue(TrailingTextProperty);
        set => SetValue(TrailingTextProperty, value);
    }

    /// <summary>Whether to display the pin marker; changing it does not persist a pin or invoke a command.</summary>
    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    /// <summary>Optional physical-controller render plan; null restores the textual activation hint.</summary>
    internal PhysicalGlyphRenderPlan? TrailingGlyph
    {
        get => GetValue(TrailingGlyphProperty);
        set => SetValue(TrailingGlyphProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == DescriptionProperty ||
            change.Property == IconGeometryProperty || change.Property == TrailingTextProperty ||
            change.Property == TrailingGlyphProperty || change.Property == IsPinnedProperty ||
            change.Property == ForegroundProperty)
        {
            RefreshPresentation();
        }
    }

    private void RefreshPresentation()
    {
        _title.Text = Title;
        _description.Text = Description;
        _description.IsVisible = !string.IsNullOrEmpty(Description);
        _icon.Data = IconGeometry;
        _icon.IsVisible = IconGeometry is not null;
        _icon.Stroke = _pin.Stroke = Foreground;
        _pin.IsVisible = IsPinned;
        _glyph.Plan = TrailingGlyph;
        _glyph.IsVisible = TrailingGlyph is not null;
        _hint.Text = TrailingText;
        _hint.IsVisible = TrailingGlyph is null && !string.IsNullOrEmpty(TrailingText);
        AutomationProperties.SetName(this, Title ?? string.Empty);
    }
}
