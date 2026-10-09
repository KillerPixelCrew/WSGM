namespace WSGM.Device.Sdk.Packaging;

/// <summary>
///     The parser bounds applied to every <see cref="WSGM.Plugin.Sdk.PluginManifest" /> before it is trusted.
/// </summary>
/// <remarks>
///     A manifest is untrusted input from a package in the protected Plugins folder. WSGM parses it before loading common
///     plugin code, so the document's size and nesting are
///     bounded before decoding starts. The size bound already bounds every field inside it, so fields
///     are checked for shape, not for length or count.
/// </remarks>
public static class ManifestLimits
{
    /// <summary>Largest accepted <c>plugin.wsgm.json</c> payload, in bytes.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>Maximum nesting depth accepted while parsing.</summary>
    public const int MaxDepth = 16;
}
