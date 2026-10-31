namespace WSGM.Core;

/// <summary>Where Steam loads one piece of artwork from.</summary>
/// <param name="Url">A data URI of the package's bytes, or the path of a glyph Steam itself ships.</param>
internal sealed record SteamInputGlyphAssetReference(string Url);
