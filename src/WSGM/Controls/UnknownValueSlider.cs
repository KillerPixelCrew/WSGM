using System;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Automation.Peers;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace WSGM.Controls;

/// <summary>A slider that can hold no known value and say so, while it stays adjustable.</summary>
/// <remarks>
///     A slider always has a position, so an unknown value would otherwise read as wherever the thumb
///     rests: the minimum, or zero. While <see cref="IsValueUnknown" /> is set the thumb and the filled
///     track are not drawn, and assistive technology hears "Unknown" instead of a number. The first
///     change the user makes draws the thumb where they put it. Steam's rows do the same in the toolkit's
///     settings renderer.
/// </remarks>
internal sealed class UnknownValueSlider : Slider
{
    /// <summary>Defines <see cref="IsValueUnknown" />.</summary>
    internal static readonly StyledProperty<bool> IsValueUnknownProperty =
        AvaloniaProperty.Register<UnknownValueSlider, bool>(nameof(IsValueUnknown));

    private const string UnknownClass = ":unknown";

    static UnknownValueSlider()
    {
        IsValueUnknownProperty.Changed.AddClassHandler<UnknownValueSlider>((slider, change) =>
            slider.PseudoClasses.Set(UnknownClass, change.GetNewValue<bool>()));
    }

    internal UnknownValueSlider()
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

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Slider);

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new Peer(this);
    }

    /// <summary>Reports a number only while one is known; otherwise the read-only value "Unknown".</summary>
    private sealed class Peer(UnknownValueSlider owner) : SliderAutomationPeer(owner), IValueProvider
    {
        bool IValueProvider.IsReadOnly => true;

        string IValueProvider.Value => "Unknown";

        void IValueProvider.SetValue(string? value)
        {
            throw new InvalidOperationException("Move the slider to set a value.");
        }

        protected override object? GetProviderCore(Type providerType)
        {
            if (!owner.IsValueUnknown)
            {
                return providerType == typeof(IValueProvider) ? null : base.GetProviderCore(providerType);
            }

            return providerType == typeof(IRangeValueProvider) ? null : base.GetProviderCore(providerType);
        }
    }
}
