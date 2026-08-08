using System;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Resolved position of one boot-splash element, produced by
/// <see cref="SplashStyle.MapPlacement"/>. Exactly one positioning scheme is
/// meaningful per instance: alignment + margin when <see cref="IsAbsolute"/> is
/// false (host the element in an alignment container), Canvas coordinates when
/// it is true (host the element on a Canvas).</summary>
internal readonly struct SplashElementLayout
{
    /// <summary>True = position via <see cref="CanvasX"/>/<see cref="CanvasY"/>;
    /// false = position via alignment + margin.</summary>
    public bool IsAbsolute { get; init; }

    /// <summary>Horizontal alignment inside the splash panel (anchor mode).</summary>
    public HorizontalAlignment HorizontalAlignment { get; init; }

    /// <summary>Vertical alignment inside the splash panel (anchor mode).</summary>
    public VerticalAlignment VerticalAlignment { get; init; }

    /// <summary>Edge padding applied on the anchored axes only (anchor mode).</summary>
    public Thickness Margin { get; init; }

    /// <summary>Canvas.Left in logical pixels, clamped into the screen (absolute mode).</summary>
    public double CanvasX { get; init; }

    /// <summary>Canvas.Top in logical pixels, clamped into the screen (absolute mode).</summary>
    public double CanvasY { get; init; }
}

/// <summary>Pure mapping helpers between <see cref="SplashConfig"/> values and
/// Avalonia layout primitives. No UI dependencies beyond struct/enum types, so
/// everything here is unit-testable.</summary>
internal static class SplashStyle
{
    /// <summary>Minimum bottom margin for bottom-row anchors, keeping elements
    /// clear of the splash's "Switch to desktop" button (Margin 0,0,28,24 +
    /// MinHeight 44 occupies roughly the bottom 68 px, plus breathing room).</summary>
    internal const double BottomClearance = 128;

    /// <summary>Parses a user-supplied color string, falling back on bad input.
    /// Never throws: null/empty input silently yields the fallback; a non-empty
    /// unparsable value logs a warning first (a bad color must never break the
    /// boot cover).</summary>
    /// <param name="text">User-supplied color string, e.g. <c>#RRGGBB</c>.</param>
    /// <param name="fallback">Color used when the string cannot be parsed.</param>
    internal static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (Color.TryParse(text.Trim(), out var color))
        {
            return color;
        }

        Log.Warn($"Splash: color '{text}' is not a valid color, using {fallback}.");
        return fallback;
    }

    /// <summary>Maps a configured element placement to concrete layout values.
    /// Anchor mode (and <see cref="SplashPlacementMode.WithText"/>, which callers
    /// are expected to divert before calling) yields alignment + margin: padding
    /// applies from the anchored edges only and is ignored on centered axes, and
    /// bottom-row anchors get at least <see cref="BottomClearance"/> px of bottom
    /// margin to clear the desktop button. Absolute mode yields Canvas
    /// coordinates clamped so the element stays inside the screen.</summary>
    /// <param name="placement">The configured placement to resolve.</param>
    /// <param name="screenSize">Logical screen size the splash covers.</param>
    /// <param name="elementHint">Estimated logical size of the element, used to
    /// keep absolute placements fully on screen.</param>
    internal static SplashElementLayout MapPlacement(
        SplashElementPlacement placement, Size screenSize, Size elementHint)
    {
        if (placement.Mode == SplashPlacementMode.Absolute)
        {
            // Clamp into [0, screen - element]; Max last so an element larger
            // than the screen pins to the top/left edge instead of going negative.
            var x = Math.Max(0, Math.Min(placement.X, screenSize.Width - elementHint.Width));
            var y = Math.Max(0, Math.Min(placement.Y, screenSize.Height - elementHint.Height));
            return new SplashElementLayout { IsAbsolute = true, CanvasX = x, CanvasY = y };
        }

        // Negative padding would push the element off screen — treat as zero.
        var paddingX = Math.Max(0, placement.PaddingX);
        var paddingY = Math.Max(0, placement.PaddingY);

        var horizontal = placement.Anchor switch
        {
            SplashPlacementAnchor.TopLeft or SplashPlacementAnchor.CenterLeft
                or SplashPlacementAnchor.BottomLeft => HorizontalAlignment.Left,
            SplashPlacementAnchor.TopRight or SplashPlacementAnchor.CenterRight
                or SplashPlacementAnchor.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        var vertical = placement.Anchor switch
        {
            SplashPlacementAnchor.TopLeft or SplashPlacementAnchor.TopCenter
                or SplashPlacementAnchor.TopRight => VerticalAlignment.Top,
            SplashPlacementAnchor.BottomLeft or SplashPlacementAnchor.BottomCenter
                or SplashPlacementAnchor.BottomRight => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };

        var left = horizontal == HorizontalAlignment.Left ? paddingX : 0;
        var right = horizontal == HorizontalAlignment.Right ? paddingX : 0;
        var top = vertical == VerticalAlignment.Top ? paddingY : 0;
        var bottom = vertical == VerticalAlignment.Bottom ? Math.Max(paddingY, BottomClearance) : 0;

        return new SplashElementLayout
        {
            IsAbsolute = false,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = new Thickness(left, top, right, bottom),
        };
    }
}
