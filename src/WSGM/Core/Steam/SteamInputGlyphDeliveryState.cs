using System.Threading;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>
///     The active handheld glyph profile, resolved into what Steam needs.
/// </summary>
/// <remarks>
///     Holds the plugin's profile, never WSGM artwork. <see cref="SteamGlyphCss" /> turns whatever is
///     here into a stylesheet; this type owns only the resolution from a plugin package to Valve
///     resource names and data URIs.
/// </remarks>
internal sealed class SteamInputGlyphDeliveryState
{
    private SteamInputGlyphPresentation? _presentation;

    /// <summary>The resolved artwork and control availability, or null without an active profile.</summary>
    internal SteamInputGlyphPresentation? Current => Volatile.Read(ref _presentation);

    /// <summary>Replaces the active profile.</summary>
    /// <param name="profile">The plugin's imported profile, or null for native presentation.</param>
    /// <param name="nativeArtwork">Keeps control availability while omitting artwork overrides.</param>
    internal void Update(ImportedGlyphProfile? profile, bool nativeArtwork = false)
    {
        var presentation = SteamInputGlyphPresentation.Create(profile);
        Volatile.Write(ref _presentation, nativeArtwork && presentation is not null
            ? presentation with { StableResources = [], ControllerImages = [] }
            : presentation);
    }
}
