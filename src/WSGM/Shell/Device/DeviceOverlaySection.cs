namespace WSGM.Shell;

/// <summary>Stable final-overlay section selected from a semantic capability role.</summary>
/// <remarks>
///     Each section is a page in the Device destination, not a heading in one long list. The split is
///     driven by capability role alone, so a plugin that publishes nothing for a section simply causes
///     that page to be absent — no section is a fixed fixture of the UI.
/// </remarks>
internal enum DeviceOverlaySection
{
    /// <summary>Identity, scenario mode, and anything that describes the device as a whole.</summary>
    Overview,

    /// <summary>Named hardware profiles the user selects between.</summary>
    Profiles,

    /// <summary>Power limits, fans, charge behaviour, and how the device performs.</summary>
    PowerAndThermals,

    /// <summary>
    ///     The physical controller and everything that describes it, glyph artwork included.
    /// </summary>
    /// <remarks>Includes glyph artwork and the live physical-input preview.</remarks>
    ControllerAndMotion,

    /// <summary>Device-specific OEM buttons and their assignments.</summary>
    Oem,

    /// <summary>Lighting and any remaining device features.</summary>
    LightingAndFeatures,

    /// <summary>Health, recovery, and anything that exists to be read rather than changed.</summary>
    Diagnostics
}
