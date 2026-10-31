using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>Selected physical profile and fallback diagnostics for all glyph consumers.</summary>
/// <param name="Profile">Shared immutable imported profile, or null to use native Steam presentation.</param>
/// <param name="FallbackReason">Reason no physical profile was selected, or None on success.</param>
/// <param name="FellBackFromMissingManualProfile">
///     Whether a missing or incompatible manual selection required automatic
///     fallback.
/// </param>
internal sealed record PhysicalGlyphSelectionResult(
    ImportedGlyphProfile? Profile,
    PhysicalGlyphFallbackReason FallbackReason,
    bool FellBackFromMissingManualProfile);
