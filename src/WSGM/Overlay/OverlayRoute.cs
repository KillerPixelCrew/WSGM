namespace WSGM.Overlay;

/// <summary>A navigation-stack entry with a semantic focus target for its caller.</summary>
/// <param name="Page">Stable page identifier.</param>
/// <param name="ReturnFocusKey">Optional semantic control key restored when returning from this route.</param>
/// <param name="SectionId">Dynamic plugin/section identity where the page route requires one.</param>
internal readonly record struct OverlayRoute(
    OverlayPage Page,
    string? ReturnFocusKey,
    string? SectionId = null);
