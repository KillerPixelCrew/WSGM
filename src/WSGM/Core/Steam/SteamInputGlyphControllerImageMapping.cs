namespace WSGM.Core;

/// <summary>One full or partial controller-diagram resource.</summary>
/// <param name="Slot">Diagram slot: full, left, or right.</param>
/// <param name="Asset">Resolved immutable artwork reference.</param>
internal sealed record SteamInputGlyphControllerImageMapping(
    string Slot,
    SteamInputGlyphAssetReference Asset);
