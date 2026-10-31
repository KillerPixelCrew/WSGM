using Avalonia;
using Avalonia.Layout;

namespace WSGM.Shell;

/// <summary>
///     Resolved position of one boot-splash element, produced by
///     <see cref="SplashStyle.MapPlacement" />. Exactly one positioning scheme is
///     meaningful per instance: alignment + margin when <see cref="IsAbsolute" /> is
///     false (host the element in an alignment container), Canvas coordinates when
///     it is true (host the element on a Canvas).
/// </summary>
internal readonly struct SplashElementLayout
{
    /// <summary>
    ///     True = position via <see cref="CanvasX" />/<see cref="CanvasY" />;
    ///     false = position via alignment + margin.
    /// </summary>
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
