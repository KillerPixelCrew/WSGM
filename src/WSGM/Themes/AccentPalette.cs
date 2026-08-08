using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FluentAvalonia.Styling;

namespace WSGM.Themes;

/// <summary>Runtime accent-color pipeline. Parses the configured accent string and
/// applies it to the running application: FluentAvalonia regenerates its accent
/// shades via <c>CustomAccentColor</c>, and the <c>Hc*</c> accent resource family in
/// <c>Palette.axaml</c> is shadowed in <c>Application.Resources</c> so every
/// DynamicResource consumer re-resolves live.</summary>
public static class AccentPalette
{
    /// <summary>The default WSGM accent (Handheld Companion orange), used when the
    /// configured value is missing or unparsable.</summary>
    public const string DefaultAccent = "#FFFF9D3D";

    /// <summary>Parses a configured accent color string.</summary>
    /// <param name="value">The configured color text (e.g. "#FF9D3D"), or null.</param>
    /// <returns>The parsed color, or the default accent when the value is missing or invalid.</returns>
    public static Color Parse(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color))
        {
            return color;
        }
        return Color.Parse(DefaultAccent);
    }

    /// <summary>Applies the accent color to the application's theme and accent resources.</summary>
    /// <param name="app">The running Avalonia application.</param>
    /// <param name="accent">The accent color to apply.</param>
    public static void Apply(Application app, Color accent)
    {
        var theme = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (theme is not null)
        {
            theme.CustomAccentColor = accent;
        }

        var onAccent = UseBlackForeground(accent) ? Colors.Black : Colors.White;
        var onAccentCaption = new Color(0xCC, onAccent.R, onAccent.G, onAccent.B);

        app.Resources["HcAccentColor"] = accent;
        app.Resources["HcAccentBrush"] = new ImmutableSolidColorBrush(accent);
        app.Resources["HcOnAccentBrush"] = new ImmutableSolidColorBrush(onAccent);
        app.Resources["HcOnAccentCaptionBrush"] = new ImmutableSolidColorBrush(onAccentCaption);
    }

    /// <summary>Decides whether black text is more legible than white on the given
    /// accent. Black wins when its WCAG contrast ratio against the accent exceeds
    /// white's, which reduces to relative luminance &gt; 0.1791.</summary>
    internal static bool UseBlackForeground(Color accent) => RelativeLuminance(accent) > 0.1791;

    /// <summary>WCAG relative luminance of an sRGB color (0 = black, 1 = white).</summary>
    internal static double RelativeLuminance(Color color)
    {
        return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
