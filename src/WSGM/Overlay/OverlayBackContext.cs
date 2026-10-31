namespace WSGM.Overlay;

/// <summary>Snapshot of higher-priority UI consumed by the pure Back policy.</summary>
/// <param name="DialogOpen">A dialog currently has priority.</param>
/// <param name="SurfaceOpen">An in-window utility or keyboard is active.</param>
/// <param name="NestedLevel">The current service view can consume one nested Back.</param>
/// <param name="FocusInRail">Focus already rests in the persistent section rail.</param>
/// <param name="RailCanTakeFocus">The current page has a section rail to return focus to.</param>
internal readonly record struct OverlayBackContext(
    bool DialogOpen,
    bool SurfaceOpen = false,
    bool NestedLevel = false,
    bool FocusInRail = false,
    bool RailCanTakeFocus = false);
