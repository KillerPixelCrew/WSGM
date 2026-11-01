namespace WSGM.Core;

/// <summary>How a boot-splash element is positioned on screen.</summary>
public enum SplashPlacementMode
{
    /// <summary>
    ///     Positioned by a nine-grid anchor plus edge padding — the portable
    ///     option that adapts to any screen size.
    /// </summary>
    Anchor,

    /// <summary>
    ///     Positioned at absolute logical pixel coordinates (device-specific;
    ///     anchors are the portable option).
    /// </summary>
    Absolute,

    /// <summary>
    ///     Rendered inside the text stack (spinner/logo only), following the
    ///     text element wherever it is placed.
    /// </summary>
    WithText
}
