using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Automation.Peers;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace WSGM.Controls;

/// <summary>A slider that can hold no known value and say so, while it stays adjustable.</summary>
/// <remarks>
///     A slider always has a position, so an unknown value would otherwise read as wherever the thumb
///     rests: the minimum, or zero. While <see cref="IsValueUnknown" /> is set the thumb and the filled
///     track are not drawn, and assistive technology hears "Unknown" instead of a number. The first
///     change the user makes draws the thumb where they put it. Steam's rows do the same in the toolkit's
///     settings renderer. <see cref="ValueText" /> lets a known value be read with its unit.
/// </remarks>
internal sealed class UnknownValueSlider : Slider
{
    /// <summary>Defines <see cref="IsValueUnknown" />.</summary>
    internal static readonly StyledProperty<bool> IsValueUnknownProperty =
        AvaloniaProperty.Register<UnknownValueSlider, bool>(nameof(IsValueUnknown));

    /// <summary>Defines <see cref="ValueText" />.</summary>
    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<UnknownValueSlider, string?>(nameof(ValueText));

    private const string UnknownClass = ":unknown";

    static UnknownValueSlider()
    {
        IsValueUnknownProperty.Changed.AddClassHandler<UnknownValueSlider>((slider, change) =>
        {
            slider.PseudoClasses.Set(UnknownClass, change.GetNewValue<bool>());
            slider.SquareJoint();
        });
        // The focused slider says where it moved. A value refreshed under an unfocused slider is not spoken.
        ValueTextProperty.Changed.AddClassHandler<UnknownValueSlider>((slider, change) =>
        {
            if (slider.IsFocused && !slider.IsValueUnknown)
            {
                ScreenReader.Announce(slider, change.GetNewValue<string?>(), "wsgm.slider-value");
            }
        });
    }

    private RepeatButton? _decrease;
    private RepeatButton? _increase;

    public UnknownValueSlider()
    {
        // No thumb, and no gap where it would sit: the track reads as one unfilled line.
        Styles.Add(new Style(selector => selector.OfType<Slider>().Class(UnknownClass).Template().OfType<Thumb>())
        {
            Setters = { new Setter(OpacityProperty, 0d), new Setter(WidthProperty, 0d) }
        });
        // The filled part before the value takes the unfilled track's brush.
        Styles.Add(new Style(selector => selector.OfType<Slider>().Class(UnknownClass).Template()
            .OfType<RepeatButton>().Name("PART_DecreaseButton"))
        {
            Setters = { new Setter(BackgroundProperty, new DynamicResourceExtension("SliderTrackFill")) }
        });
    }

    /// <summary>Whether the value is unknown: no thumb, no fill, and "Unknown" for assistive technology.</summary>
    internal bool IsValueUnknown
    {
        get => GetValue(IsValueUnknownProperty);
        set => SetValue(IsValueUnknownProperty, value);
    }

    /// <summary>The value as the row shows it, with its unit ("17 W"), for assistive technology.</summary>
    /// <remarks>
    ///     A screen reader reads a bare range value as a percentage of the range, so a 5 to 30 W slider at
    ///     17 W would say "48". With this set the slider reports the text instead, and the focused slider
    ///     announces each new value. Null reports the plain number.
    /// </remarks>
    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Slider);

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Watch(ref _decrease, e.NameScope.Find<RepeatButton>("PART_DecreaseButton"));
        Watch(ref _increase, e.NameScope.Find<RepeatButton>("PART_IncreaseButton"));
        SquareJoint();
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new Peer(this);
    }

    private void Watch(ref RepeatButton? field, RepeatButton? button)
    {
        if (field is not null)
        {
            field.TemplateApplied -= OnPartTemplateApplied;
        }

        field = button;
        if (button is not null)
        {
            button.TemplateApplied += OnPartTemplateApplied;
        }
    }

    private void OnPartTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        SquareJoint();
    }

    /// <summary>Squares the two track halves where they meet while the value is unknown.</summary>
    /// <remarks>
    ///     Each half of the track rounds both of its ends, and with no thumb between them the two rounded
    ///     ends meet in the middle of the line as a darker notch. The radius comes from the template, so it
    ///     is overridden locally while unknown and cleared again once a value is shown.
    /// </remarks>
    private void SquareJoint()
    {
        Square(_decrease, right: true);
        Square(_increase, right: false);

        void Square(RepeatButton? part, bool right)
        {
            if (part?.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "TrackBackground")
                is not { } track)
            {
                return;
            }

            track.ClearValue(Border.CornerRadiusProperty);
            if (!IsValueUnknown)
            {
                return;
            }

            var radius = track.CornerRadius;
            track.CornerRadius = right
                ? new CornerRadius(radius.TopLeft, 0, 0, radius.BottomLeft)
                : new CornerRadius(0, radius.TopRight, radius.BottomRight, 0);
        }
    }

    /// <summary>
    ///     Reports "Unknown" while no value is known, the value text while one is set, and otherwise the
    ///     plain number. A text value replaces the range value, which readers turn into a percentage.
    /// </summary>
    private sealed class Peer(UnknownValueSlider owner) : SliderAutomationPeer(owner), IValueProvider
    {
        bool IValueProvider.IsReadOnly => true;

        string IValueProvider.Value => owner.IsValueUnknown ? "Unknown" : owner.ValueText ?? string.Empty;

        void IValueProvider.SetValue(string? value)
        {
            throw new InvalidOperationException("Move the slider to set a value.");
        }

        protected override object? GetProviderCore(Type providerType)
        {
            var spoken = owner.IsValueUnknown || !string.IsNullOrEmpty(owner.ValueText);
            if (!spoken)
            {
                return providerType == typeof(IValueProvider) ? null : base.GetProviderCore(providerType);
            }

            return providerType == typeof(IRangeValueProvider) ? null : base.GetProviderCore(providerType);
        }
    }
}
