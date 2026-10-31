namespace WSGM.Core;

/// <summary>Reason a physical-device glyph surface must retain or return to native Steam artwork.</summary>
internal enum PhysicalGlyphFallbackReason
{
    /// <summary>A matching reviewed physical profile was selected.</summary>
    None,

    /// <summary>Device integration is disabled, so no device artwork is eligible.</summary>
    DeviceIntegrationDisabled,

    /// <summary>The user explicitly selected Steam's native glyphs.</summary>
    NativeSteamSelected,

    /// <summary>No profile matches the active exact device definition.</summary>
    ExactDeviceMismatch,

    /// <summary>The input source does not identify the integrated handheld controls.</summary>
    SourceNotHandheld,

    /// <summary>The selected profile does not declare this physical control.</summary>
    ControlAbsent,

    /// <summary>Declared artwork could not be found.</summary>
    ArtworkMissing,

    /// <summary>The consuming surface rejected the supplied artwork.</summary>
    RenderRejected
}
