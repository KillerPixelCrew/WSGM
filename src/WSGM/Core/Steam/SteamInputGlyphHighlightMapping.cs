using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>The overlay lit on the controller diagram while one physical control is selected.</summary>
/// <param name="Control">The physical control.</param>
/// <param name="Asset">Its highlight artwork, in the full-controller image's coordinate space.</param>
internal sealed record SteamInputGlyphHighlightMapping(
    GlyphControlId Control,
    SteamInputGlyphAssetReference Asset);
