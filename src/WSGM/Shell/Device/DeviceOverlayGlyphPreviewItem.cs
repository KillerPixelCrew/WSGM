using WSGM.Controls;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>One control in the glyph preview.</summary>
/// <param name="Control">The physical control this glyph stands for.</param>
/// <param name="Label">Human-readable control name.</param>
/// <param name="Plan">The resolved artwork, or a plan that carries none.</param>
internal sealed record DeviceOverlayGlyphPreviewItem(
    GlyphControlId Control,
    string Label,
    PhysicalGlyphRenderPlan Plan);
