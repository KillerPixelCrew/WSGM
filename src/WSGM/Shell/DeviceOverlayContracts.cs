using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;

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

/// <summary>One presentation-only semantic capability row for the final Device destination.</summary>
/// <param name="CapabilityId">Published capability ID used for command routing.</param>
/// <param name="InstanceId">Published instance discriminator, or null for a single-instance capability.</param>
/// <param name="Section">Host fallback page selected from semantic role.</param>
/// <param name="Status">Projected availability or command status for presentation.</param>
/// <param name="Title">Resolved plain row label.</param>
/// <param name="Description">Resolved explanatory text, empty when absent.</param>
/// <param name="TrailingText">Formatted value or status shown beside the label.</param>
/// <param name="CanInvoke">Whether current host admission permits interaction; dispatch still revalidates.</param>
/// <param name="CurrentValue">Current effective value, or null when unknown or action-only.</param>
/// <param name="NextValue">Suggested value for a toggle/cycle action, or null when a separate editor supplies it.</param>
internal sealed record DeviceOverlayCapability(
    string CapabilityId,
    string? InstanceId,
    DeviceOverlaySection Section,
    DescriptorStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    CapabilityValue? CurrentValue = null,
    CapabilityValue? NextValue = null)
{
    /// <summary>Descriptor identity used to invalidate layout independently of state updates.</summary>
    public long DescriptorGeneration { get; init; }

    /// <summary>
    ///     The profile setting id while the running game's own profile supplies this value, else null.
    ///     The row marks it and offers Use global.
    /// </summary>
    public string? OverrideId { get; init; }

    /// <summary>Device cycle that owns this descriptor.</summary>
    public long CycleGeneration { get; init; }

    /// <summary>
    ///     The graphics plugin that publishes this row, or null for the device package. Variable refresh is
    ///     the one graphics capability the Device page shows.
    /// </summary>
    public string? GpuPluginId { get; init; }

    /// <summary>Host-owned layout emphasis.</summary>
    public CapabilityProminence Prominence { get; init; }

    /// <summary>Optional companion identity within the same group.</summary>
    public CapabilityLayoutPair? LayoutPair { get; init; }

    /// <summary>The descriptor's semantic role, for consumers pairing related controls.</summary>
    public CapabilityRole Role { get; init; } = CapabilityRole.GenericReadOnly;

    /// <summary>The declared overlay section holding this row, or null for the WSGM fallback home.</summary>
    public string? PluginSectionId { get; init; }

    /// <summary>The declared category within that section, or null for the section's lead group.</summary>
    public string? CategoryId { get; init; }

    /// <summary>Placement within its section and category.</summary>
    public int SortOrder { get; init; }

    /// <summary>
    ///     What kind of value this capability carries, so the overlay picks a control:
    ///     integer range → slider, choice → dropdown, boolean → toggle, text → textbox, color →
    ///     swatch/editor, otherwise a plain action button.
    /// </summary>
    public CapabilityValueKind ValueKind { get; init; } = CapabilityValueKind.None;

    /// <summary>
    ///     Whether the current value may be written. A read-only ranged capability still shows
    ///     its value but renders as a reading, not an adjustable control.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>Whether the descriptor represents a one-shot action even while unavailable.</summary>
    public bool SupportsAction { get; init; }

    /// <summary>Inclusive lower bound of an integer capability, or null when it has no range.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive upper bound of an integer capability, or null when it has no range.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values; at least 1 when a range is present.</summary>
    public int? Step { get; init; }

    /// <summary>Unit for a numeric value, used to format the slider's live label.</summary>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>The ordered legal values of a choice capability, empty otherwise.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>Maximum text length for a text capability, or null when it has none.</summary>
    public int? MaximumLength { get; init; }
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

/// <summary>Complete bounded Device-surface snapshot produced from coordinator-owned state.</summary>
/// <remarks>
///     The direct rows are WSGM's own controls — AutoTDP moves the plugin's power limit rather than
///     being one, the controller target and glyph selection are WSGM settings, the profile rows are
///     stored configuration, and recovery is an action on the cycle itself. Each carries its stable
///     focus key as the <see cref="DescriptorRow.Id" />, and a null row is simply not shown.
/// </remarks>
/// <param name="Visible">Whether the surface currently exposes device content.</param>
/// <param name="Status">Aggregate display status.</param>
/// <param name="Detail">Plain explanation of lifecycle or availability state.</param>
/// <param name="GlyphSelection">Host-owned glyph selection row, or null when absent.</param>
/// <param name="Capabilities">Current semantic rows, including generation stamps for revalidation.</param>
/// <param name="AutoTdp">Host-owned AutoTDP row, or null when unavailable.</param>
/// <param name="Controller">Controller target row, or null when absent.</param>
/// <param name="Recovery">Explicit cycle recovery action, or null when no recovery is offered.</param>
/// <param name="GlyphPreview">Artwork and physical-input preview, or null when absent.</param>
/// <param name="AuthoredProfile">Authored fan-profile selector, or null when absent.</param>
internal sealed record DeviceOverlaySnapshot(
    bool Visible,
    string Status,
    string Detail,
    DescriptorRow? GlyphSelection,
    IReadOnlyList<DeviceOverlayCapability> Capabilities,
    DescriptorRow? AutoTdp = null,
    DescriptorRow? Controller = null,
    DescriptorRow? Recovery = null,
    DeviceOverlayGlyphPreview? GlyphPreview = null,
    DescriptorRow? AuthoredProfile = null)
{
    /// <summary>Explicit host-owned choices, keyed by stable row identity.</summary>
    public IReadOnlyDictionary<string, DeviceHostSelection> HostSelections { get; init; } =
        new Dictionary<string, DeviceHostSelection>();

    /// <summary>The selected physical button presentation policy.</summary>
    public DeviceGlyphSelection GlyphMode { get; init; }

    /// <summary>Plugin-declared overlay sections in presentation order.</summary>
    public IReadOnlyList<DeviceOverlayPluginSection> PluginSections { get; init; } =
        DeviceOverlayBridge.ProjectSections(DeviceSections.All);
}

/// <summary>Stable ids of the host-owned Device rows, shared by the bridge and the overlay.</summary>
internal static class DeviceHostRowIds
{
    public const string AutoTdp = "device.auto-tdp";
    public const string ControllerTarget = "device.controller-target";
    public const string AuthoredProfile = "device.authored-profile";
    public const string Retry = "device.retry";
    public const string GlyphSelection = "device.glyph-selection";
}

/// <summary>Named choices and current selection for one host-owned setting.</summary>
/// <param name="Value">Selected machine value, or null for no selection.</param>
/// <param name="Choices">Ordered legal choices; UI sends the selected value without replaying intermediate options.</param>
internal sealed record DeviceHostSelection(string? Value, IReadOnlyList<CapabilityChoice> Choices);

/// <summary>Closed semantic source consumed by the Device overlay destination.</summary>
internal interface IDeviceOverlaySource : IDisposable
{
    /// <summary>Signals that consumers should read a new snapshot; input-rate samples use their separate event.</summary>
    event Action? Changed;

    /// <summary>Raised for each physical sample while the glyph input test is observing.</summary>
    /// <remarks>
    ///     Separate from <see cref="Changed" /> because it fires at input rate. A consumer must treat it
    ///     as a hint to update one visual state, never as a reason to rebuild a page.
    /// </remarks>
    event Action<CanonicalControllerSample>? PhysicalSampleReceived;

    /// <summary>The device's own glyph for one navigation hint, when one applies.</summary>
    /// <param name="control">The control the hint names.</param>
    /// <returns>The glyph to draw, or null to keep the written letter.</returns>
    /// <remarks>
    ///     Null is the normal answer on most machines, and the caller must treat it as "show the letter"
    ///     rather than "show nothing". The hint is only replaced when the input actually reaching WSGM
    ///     is the managed handheld's, because a hint showing a Claw button while the user is holding an
    ///     Xbox pad is worse than the letter it replaced.
    /// </remarks>
    PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control);

    /// <summary>Starts delivering physical samples for the glyph input test.</summary>
    /// <returns>A lease that stops delivery when disposed.</returns>
    /// <remarks>
    ///     Leased rather than always-on: the samples exist to light a preview that is on one page, and
    ///     nothing else on the Device surface wants an input-rate event.
    /// </remarks>
    IDisposable ObservePhysicalSamples();

    /// <summary>Projects coordinator-owned state for the Device surface without hardware I/O.</summary>
    /// <returns>The current presentation snapshot; null optional rows are omitted by the view.</returns>
    DeviceOverlaySnapshot Snapshot();

    /// <summary>Dispatches the row's action or suggested next value through its owning capability router.</summary>
    /// <param name="capability">Snapshot row carrying identity, generations and requested value.</param>
    /// <param name="cancellationToken">Cancels waiting; a dispatched hardware write may still complete.</param>
    /// <returns>Completion after the command attempt and host projection update; does not itself prove hardware readback.</returns>
    Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default);

    /// <summary>Persists the host's physical-button artwork policy and republishes presentation.</summary>
    /// <param name="selection">Declared physical glyph policy.</param>
    /// <param name="cancellationToken">Cancels configuration persistence or reconciliation.</param>
    /// <returns>Completion after the requested host setting is applied; no device firmware is written.</returns>
    Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection, CancellationToken cancellationToken = default);

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies one explicit host setting selection without cycling through intermediate writes.</summary>
    /// <param name="rowId">Stable host-owned row ID, such as controller target or authored profile.</param>
    /// <param name="value">Selected machine value; null clears a selection where the row supports it.</param>
    /// <param name="cancellationToken">Cancels persistence or reconciliation waiting.</param>
    /// <returns>Completion after the selection attempt; downstream ownership changes retain their own outcome semantics.</returns>
    Task SetHostSelectionAsync(string rowId, string? value, CancellationToken cancellationToken = default);

    /// <summary>Moves the controller target to the next one in the layer in force and persists it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new target is persisted and applied.</returns>
    Task CycleControllerTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the running game's value for one setting, so it falls back to Global.</summary>
    /// <param name="overrideId">The id a row carried in its <c>OverrideId</c>.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing once the override is gone.</returns>
    Task UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default);

    /// <summary>Requests explicit recovery of a faulted device cycle.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A task completing once the attempt has been made.</returns>
    Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves to the next authored fan profile, or to none, and applies it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new selection is persisted and applied.</returns>
    /// <remarks>
    ///     Saved to the running game's profile while it is on, and to Global otherwise, like every
    ///     other per-game value.
    /// </remarks>
    Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default);
}
