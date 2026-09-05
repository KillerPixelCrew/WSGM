namespace WSGM.Device.Sdk.Packaging;

/// <summary>
/// Hard bounds applied to every <see cref="PluginManifest"/> before it is trusted.
/// </summary>
/// <remarks>
/// A manifest is untrusted input from the package selected for the sole protected slot. Device Lab
/// and WSGM both parse it before loading plugin code. Unbounded
/// strings are therefore a decode budget waiting to be exhausted, so every field has a ceiling and
/// exceeding one rejects the manifest rather than truncating it. The numbers are deliberately
/// generous for real packages and deliberately finite for hostile ones.
/// </remarks>
public static class ManifestLimits
{
    /// <summary>Largest accepted <c>plugin.wsgm.json</c> payload, in bytes.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>Maximum nesting depth accepted while parsing.</summary>
    public const int MaxDepth = 16;

    /// <summary>Maximum length of the stable package identifier.</summary>
    public const int MaxIdLength = 128;

    /// <summary>Maximum length of a human-readable name or free-text field.</summary>
    public const int MaxDisplayTextLength = 256;

    /// <summary>Maximum length of a relative path expressed in a manifest.</summary>
    public const int MaxPathLength = 260;
}
