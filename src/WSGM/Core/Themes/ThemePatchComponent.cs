using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WSGM.Core;

/// <summary>A colour or image the user picks under a patch option.</summary>
/// <remarks>
///     Mirrors <c>ThemePatchComponent</c> in <c>css_themepatchcomponent.py</c> (b1bc683), including
///     the generated variables: a colour is written as the value itself plus its red, green, blue and
///     comma-joined RGB forms, so a theme can use whichever it needs; an image is written as a
///     <c>url()</c> under <c>/themes_custom/</c>, the folder Steam serves the themes from.
/// </remarks>
public sealed class ThemePatchComponent
{
    private readonly string _themesRoot;
    private string _value;

    /// <summary>Creates the component from its manifest.</summary>
    /// <param name="manifest">The manifest entry.</param>
    /// <param name="targets">The expanded targets the variable is set in.</param>
    /// <param name="themesRoot">The themes folder, which an image path is relative to.</param>
    /// <param name="patchIdentity">The theme and patch the component belongs to, which name its block.</param>
    public ThemePatchComponent(
        ThemeComponentManifest manifest, IReadOnlyList<string> targets, string themesRoot, string patchIdentity)
    {
        _themesRoot = themesRoot;
        Name = manifest.Name;
        Type = manifest.Type;
        On = manifest.On;
        CssVariable = manifest.CssVariable;
        Default = manifest.Default;
        if (Type == "color-picker" && CheckColor(Default) is { } refusal)
        {
            Log.Warn($"Theme component '{Name}': {refusal}");
            Default = "#FFFFFF";
        }

        _value = Default;
        Inject = new ThemeInject(targets, null, $"{patchIdentity}|{Name}");
        Generate();
    }

    /// <summary>The component's label.</summary>
    public string Name { get; }

    /// <summary><c>color-picker</c> or <c>image-picker</c>.</summary>
    public string Type { get; }

    /// <summary>The patch option the component belongs to.</summary>
    public string On { get; }

    /// <summary>The variable written, with its <c>--</c>.</summary>
    public string CssVariable { get; }

    /// <summary>The value until the user picks one.</summary>
    public string Default { get; }

    /// <summary>The block carrying the variable.</summary>
    public ThemeInject Inject { get; }

    /// <summary>The current colour or image path.</summary>
    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            Generate();
        }
    }

    /// <summary>Writes the block for the current value.</summary>
    /// <returns>Null, or why the value could not be written.</returns>
    public string? Generate()
    {
        if (CssVariable.Contains(';') || _value.Contains(';'))
        {
            return "???";
        }

        if (Type == "color-picker")
        {
            var value = _value;
            try
            {
                (int r, int g, int b) rgb;
                if (value.StartsWith('#'))
                {
                    rgb = HexToRgb(value);
                }
                else if ((value.StartsWith("hsla(", StringComparison.Ordinal)
                          || value.StartsWith("hsl(", StringComparison.Ordinal))
                         && value.EndsWith(')'))
                {
                    var parts = value[(value.IndexOf('(') + 1)..^1].Split(',');
                    var h = parts[0].Trim();
                    var s = parts[1].Trim()[..^1];
                    var l = parts[2].Trim()[..^1];
                    rgb = HslToRgb(
                        double.Parse(h, CultureInfo.InvariantCulture),
                        double.Parse(s, CultureInfo.InvariantCulture),
                        double.Parse(l, CultureInfo.InvariantCulture));
                }
                else
                {
                    throw new FormatException($"Unable to parse color-picker value '{value}'");
                }

                Inject.SetGenerated(
                    $":root {{ {CssVariable}: {value}; {CssVariable}_r: {rgb.r}; {CssVariable}_g: {rgb.g}; "
                    + $"{CssVariable}_b: {rgb.b}; {CssVariable}_rgb: {rgb.r}, {rgb.g}, {rgb.b}; }}");
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException
                                           or ArgumentException)
            {
                Inject.SetGenerated($":root {{ {CssVariable}: {value}; }}");
            }

            return null;
        }

        if (CheckImagePath(_value) is { } refusal)
        {
            return refusal;
        }

        var path = "/themes_custom/" + _value.Replace(" ", "%20").Replace('\\', '/');
        Inject.SetGenerated($":root {{ {CssVariable}: url({path}) }}");
        return null;
    }

    /// <summary>Whether a colour is one CSS Loader accepts as a default.</summary>
    /// <param name="value">The colour text.</param>
    /// <returns>Null, or why not.</returns>
    public static string? CheckColor(string value)
    {
        if (value.Length == 0 || value[0] != '#')
        {
            return "Color picker default is not a valid hex value";
        }

        if (value.Length is not (4 or 5 or 7 or 9))
        {
            return "Color picker default is not a valid hex value";
        }

        foreach (var character in value.AsSpan(1))
        {
            if (!Uri.IsHexDigit(character))
            {
                return "Color picker default is not a valid hex value";
            }
        }

        return null;
    }

    /// <summary>Whether an image path stays inside the themes folder and names a file.</summary>
    /// <param name="path">The path as the user or manifest wrote it.</param>
    /// <returns>Null, or why not.</returns>
    public string? CheckImagePath(string path)
    {
        if (path.Trim().StartsWith('/'))
        {
            return "Image Picker path cannot be absolute";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Trim() == "..")
            {
                return "Going back in a relative path is not allowed";
            }
        }

        if (Path.IsPathRooted(path) || path.Contains("..\\", StringComparison.Ordinal))
        {
            return "Going back in a relative path is not allowed";
        }

        return File.Exists(Path.Combine(_themesRoot, path))
            ? null
            : "Image Picker specified image does not exist";
    }

    /// <summary>The snapshot the UI reads.</summary>
    public ThemeComponentSnapshot Snapshot()
    {
        return new ThemeComponentSnapshot(Name, Type, On, _value);
    }

    // CSS Loader's own conversion, digit for digit: a short hex reads each digit as a value of its
    // own rather than doubling it, and that is what its themes were written against.
    private static (int, int, int) HexToRgb(string hex)
    {
        var digits = hex[1..];
        if (digits.Length < 6)
        {
            return (HexValue(digits[..1]), HexValue(digits[1..2]), HexValue(digits[2..3]));
        }

        return (HexValue(digits[..2]), HexValue(digits[2..4]), HexValue(digits[4..6]));
    }

    private static int HexValue(string digits)
    {
        return int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static (int, int, int) HslToRgb(double hue, double saturation, double lightness)
    {
        const double oneThird = 1.0 / 3.0;
        var h = hue / 360.0;
        var l = lightness / 100.0;
        var s = saturation / 100.0;
        if (s == 0.0)
        {
            return ((int)(l * 100.0), (int)(l * 100.0), (int)(l * 100.0));
        }

        var m2 = l <= 0.5 ? l * (1.0 + s) : l + s - l * s;
        var m1 = 2.0 * l - m2;
        return (
            (int)(Mask(m1, m2, h + oneThird) * 255.0),
            (int)(Mask(m1, m2, h) * 255.0),
            (int)(Mask(m1, m2, h - oneThird) * 255.0));
    }

    private static double Mask(double m1, double m2, double hue)
    {
        const double oneSixth = 1.0 / 6.0;
        const double twoThirds = 2.0 / 3.0;
        hue %= 1.0;
        if (hue < 0)
        {
            hue += 1.0;
        }

        if (hue < oneSixth)
        {
            return m1 + (m2 - m1) * hue * 6.0;
        }

        if (hue < 0.5)
        {
            return m2;
        }

        if (hue < twoThirds)
        {
            return m1 + (m2 - m1) * (twoThirds - hue) * 6.0;
        }

        return m1;
    }
}

/// <summary>What the UI shows of a component.</summary>
/// <param name="Name">Its label.</param>
/// <param name="Type"><c>color-picker</c> or <c>image-picker</c>.</param>
/// <param name="On">The option it belongs to.</param>
/// <param name="Value">Its current value.</param>
public sealed record ThemeComponentSnapshot(string Name, string Type, string On, string Value);
