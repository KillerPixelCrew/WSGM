using System.Collections.Generic;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Settings;

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

/// <summary>One category heading of a plugin-declared overlay section.</summary>
/// <param name="Id">Stable category ID within its section.</param>
/// <param name="Title">Resolved display heading.</param>
internal sealed record DeviceOverlayCategory(string Id, string Title);

/// <summary>One plugin-declared overlay section, projected for presentation.</summary>
/// <remarks>
///     Presentation-only: titles are already resolved from the WSGM-owned key vocabulary or bounded
///     plugin text, so no consumer of this record touches SDK display metadata again.
/// </remarks>
/// <param name="SectionId">Stable plugin section identity.</param>
/// <param name="Title">Resolved plain section title.</param>
/// <param name="Description">Resolved supporting text, empty when absent.</param>
/// <param name="Icon">Host-supported section icon.</param>
/// <param name="Categories">Category headings in declaration order; an empty list leaves a single lead group.</param>
internal sealed record DeviceOverlayPluginSection(
    string SectionId,
    string Title,
    string Description,
    SectionIcon Icon,
    IReadOnlyList<DeviceOverlayCategory> Categories)
{
    /// <summary>The subject this section claims, which is how a WSGM-owned section finds its home.</summary>
    /// <remarks>
    ///     Carried through from the declaration rather than inferred from the title: a plugin may call
    ///     its power page anything, and the key is the only part of the declaration that means the same
    ///     thing to WSGM. <see cref="SettingSectionKey.Custom" /> claims no subject and absorbs nothing.
    /// </remarks>
    public SettingSectionKey Key { get; init; } = SettingSectionKey.Custom;
}

/// <summary>One control in the glyph preview.</summary>
/// <param name="Control">The physical control this glyph stands for.</param>
/// <param name="Label">Human-readable control name.</param>
/// <param name="Plan">The resolved artwork, or a plan that carries none.</param>
internal sealed record DeviceOverlayGlyphPreviewItem(
    GlyphControlId Control,
    string Label,
    PhysicalGlyphRenderPlan Plan);

/// <summary>The glyph preview and its live input test.</summary>
/// <remarks>
///     One projection for both, because they are the same picture answering two questions: whether the
///     plugin's artwork resolves at all, and whether pressing a control reaches WSGM as the control the
///     artwork claims. Separating them would mean drawing the same map twice.
/// </remarks>
/// <param name="ProfileName">The profile supplying the artwork, or why none is.</param>
/// <param name="Detail">One line about the profile's provenance.</param>
/// <param name="Items">Every control the profile maps, in canonical order.</param>
/// <param name="InputTestAvailable">Whether physical input is reaching the surface.</param>
internal sealed record DeviceOverlayGlyphPreview(
    string ProfileName,
    string Detail,
    IReadOnlyList<DeviceOverlayGlyphPreviewItem> Items,
    bool InputTestAvailable);

/// <summary>Stable ids of the host-owned Device rows, shared by the bridge and the overlay.</summary>
internal static class DeviceHostRowIds
{
    public const string AutoTdp = "device.auto-tdp";
    public const string AutoTdpTarget = "device.auto-tdp-target";
    public const string ControllerTarget = "device.controller-target";
    public const string AuthoredProfile = "device.authored-profile";
    public const string LightingProfile = "device.lighting-profile";
    public const string Retry = "device.retry";
    public const string GlyphSelection = "device.glyph-selection";
}

/// <summary>Named choices and current selection for one host-owned setting.</summary>
/// <param name="Value">Selected machine value, or null for no selection.</param>
/// <param name="Choices">Ordered legal choices; UI sends the selected value without replaying intermediate options.</param>
internal sealed record DeviceHostSelection(string? Value, IReadOnlyList<CapabilityChoice> Choices);
