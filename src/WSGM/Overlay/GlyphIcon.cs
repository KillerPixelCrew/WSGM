using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Renders a controller button glyph from the bundled Kenney CC0 SVGs.
/// The SVGs are simple single/multi &lt;path fill d&gt; files, so they are parsed
/// directly into Avalonia geometry — no SVG library, fully AOT-safe.
/// Button names are by LABEL ("a" shows the style's A/Cross art); the confirm
/// action always displays "a" — for Nintendo the INPUT mapping swaps instead
/// (see GamepadNavigation), so the labeled-A button confirms in every style.</summary>
public sealed partial class GlyphIcon : ContentControl
{
    public static readonly StyledProperty<GlyphStyle> GlyphStyleProperty =
        AvaloniaProperty.Register<GlyphIcon, GlyphStyle>(nameof(GlyphStyle));

    public static readonly StyledProperty<string> ButtonProperty =
        AvaloniaProperty.Register<GlyphIcon, string>(nameof(Button), "a");

    [GeneratedRegex("<path[^>]*fill=\"(?<fill>[^\"]+)\"[^>]*d=\"(?<data>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex PathRegex();

    public GlyphStyle GlyphStyle
    {
        get => GetValue(GlyphStyleProperty);
        set => SetValue(GlyphStyleProperty, value);
    }

    public string Button
    {
        get => GetValue(ButtonProperty);
        set => SetValue(ButtonProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphStyleProperty || change.Property == ButtonProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Content is null)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var styleName = GlyphStyle switch
        {
            GlyphStyle.PlayStation => "playstation",
            GlyphStyle.Nintendo => "nintendo",
            _ => "xbox",
        };

        try
        {
            var uri = new Uri($"avares://WSGM/Assets/Glyphs/{styleName}/{Button}.svg");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var canvas = new Canvas { Width = 64, Height = 64 };
            foreach (Match match in PathRegex().Matches(svg))
            {
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    // Default fill rule (EvenOdd) turns inner subpaths (letters,
                    // symbols) into holes — matching how these SVGs are drawn.
                    Data = Geometry.Parse(match.Groups["data"].Value),
                    Fill = new SolidColorBrush(Color.Parse(match.Groups["fill"].Value)),
                });
            }

            Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load glyph {styleName}/{Button}", ex);
            Content = new TextBlock { Text = Button.ToUpperInvariant() };
        }
    }
}
