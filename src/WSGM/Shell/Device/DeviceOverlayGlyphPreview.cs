using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The glyph preview and its live input test.</summary>
/// <remarks>
///     One projection for both, because they are the same picture answering two questions: whether the
///     plugin's artwork resolves at all, and whether pressing a control reaches WSGM as the control the
///     artwork claims. Separating them would mean drawing the same map twice.
/// </remarks>
/// <param name="ProfileName">The profile supplying the artwork, or why none is.</param>
/// <param name="Detail">One line about the profile's provenance.</param>
/// <param name="Items">Every control the profile maps, in canonical order.</param>
/// <param name="InputTestAvailable">Whether physical input is reaching the surface.</param>
internal sealed record DeviceOverlayGlyphPreview(
    string ProfileName,
    string Detail,
    IReadOnlyList<DeviceOverlayGlyphPreviewItem> Items,
    bool InputTestAvailable);
