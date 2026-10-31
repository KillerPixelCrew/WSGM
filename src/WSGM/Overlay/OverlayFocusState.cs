namespace WSGM.Overlay;

/// <summary>Semantic focus and scroll state retained without keeping a page or control alive.</summary>
/// <param name="SemanticKey">Stable control identity, or null when no row was focused.</param>
/// <param name="ScrollOffset">Vertical scroll position in device-independent pixels.</param>
internal readonly record struct OverlayFocusState(string? SemanticKey, double ScrollOffset);
