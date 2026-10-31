using Avalonia;
using Avalonia.Layout;

namespace WSGM.Shell;

/// <summary>
///     Resolved position of one boot-splash element, produced by
///     <see cref="SplashStyle.MapPlacement" />. Exactly one positioning scheme is
///     meaningful per instance, chosen by the placement's mode: alignment + margin in
///     anchor mode (host the element in an alignment container), Canvas coordinates in
///     absolute mode (host the element on a Canvas).
/// </summary>
internal readonly struct SplashElementLayout
{
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
