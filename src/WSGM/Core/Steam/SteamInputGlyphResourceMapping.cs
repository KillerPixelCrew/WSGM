using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>One Valve glyph resource replaced by physical-device artwork.</summary>
/// <param name="ValvePath">Steam resource path matched by the stylesheet.</param>
/// <param name="Control">Logical control whose presentation is replaced.</param>
/// <param name="Asset">Resolved package data URI or native Steam glyph URL.</param>
internal sealed record SteamInputGlyphResourceMapping(
    string ValvePath,
    GlyphControlId Control,
    SteamInputGlyphAssetReference Asset);
