namespace WSGM.Device.Contracts.Packaging;

/// <summary>
/// Hard bounds applied to every <see cref="PluginManifest"/> before it is trusted.
/// </summary>
/// <remarks>
/// A manifest is untrusted input: it arrives from a package that may be sideloaded, and it is parsed
/// inside the NativeAOT WSGM process while deciding which plugin to select. Unbounded strings and
/// collections are therefore a decode budget waiting to be exhausted, so every field has a ceiling
/// and exceeding one rejects the manifest rather than truncating it. The numbers are deliberately
/// generous for real packages and deliberately finite for hostile ones.
/// </remarks>
public static class ManifestLimits
{
    /// <summary>Largest accepted <c>plugin.wsgm.json</c> payload, in bytes.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>Maximum nesting depth accepted while parsing.</summary>
    public const int MaxDepth = 16;

    /// <summary>Maximum length of a stable identifier such as a package, device, or module ID.</summary>
    public const int MaxIdLength = 128;

    /// <summary>Maximum length of a human-readable name or free-text field.</summary>
    public const int MaxDisplayTextLength = 256;

    /// <summary>Maximum length of a relative path expressed in a manifest.</summary>
    public const int MaxPathLength = 260;

    /// <summary>Maximum number of device definitions in one package.</summary>
    public const int MaxDevices = 32;

    /// <summary>Maximum number of declared resources in one device definition.</summary>
    public const int MaxResources = 32;

    /// <summary>Maximum number of declared dependencies in one package.</summary>
    public const int MaxDependencies = 32;

    /// <summary>Maximum number of implementation modules composed by one device definition.</summary>
    public const int MaxModules = 64;

    /// <summary>Maximum number of semantic capabilities declared by one device definition.</summary>
    public const int MaxCapabilities = 128;

    /// <summary>Maximum number of identity observations in one device definition.</summary>
    public const int MaxIdentityObservations = 64;

    /// <summary>Maximum number of USB endpoint declarations in one device definition.</summary>
    public const int MaxUsbEndpoints = 32;

    /// <summary>Maximum number of risk declarations in one package.</summary>
    public const int MaxRiskDeclarations = 32;

    /// <summary>Highest weight an optional identity observation may carry.</summary>
    /// <remarks>
    /// Weights only order candidates within a package that already passed every hard constraint.
    /// Capping the range keeps a manifest from expressing "this weak signal outranks everything",
    /// which is the shape a package would use to win selection it did not earn.
    /// </remarks>
    public const int MaxObservationWeight = 100;
}
