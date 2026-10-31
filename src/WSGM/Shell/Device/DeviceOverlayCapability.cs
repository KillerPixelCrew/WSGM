using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;

namespace WSGM.Shell;

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
    /// <summary>
    ///     The profile setting id while the running game's own profile supplies this value, else null.
    ///     The row marks it and offers Use global.
    /// </summary>
    public string? OverrideId { get; init; }

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

    /// <summary>The preset shapes a fan curve editor offers for this device, or null for other rows.</summary>
    public FanCurvePresetTable? Presets { get; init; }

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

    /// <summary>Starting inputs for a writable curve whose current value is unknown.</summary>
    public IReadOnlyList<int> CurveInputs { get; init; } = [];

    /// <summary>Maximum text length for a text capability, or null when it has none.</summary>
    public int? MaximumLength { get; init; }

    /// <summary>Whether a mounted control can retain its layout and editor without stale bounds.</summary>
    internal bool SameLayoutAs(DeviceOverlayCapability other)
    {
        return CapabilityId == other.CapabilityId
               && InstanceId == other.InstanceId && GpuPluginId == other.GpuPluginId && Role == other.Role
               && ValueKind == other.ValueKind && Writable == other.Writable && SupportsAction == other.SupportsAction
               && Minimum == other.Minimum && Maximum == other.Maximum && Step == other.Step && Unit == other.Unit
               && MaximumLength == other.MaximumLength && Choices.SequenceEqual(other.Choices)
               && CurveInputs.SequenceEqual(other.CurveInputs)
               && Prominence == other.Prominence && LayoutPair == other.LayoutPair && SortOrder == other.SortOrder
               && Title == other.Title && CategoryId == other.CategoryId && PluginSectionId == other.PluginSectionId;
    }

    /// <summary>Whether two row lists keep their controls, row by row.</summary>
    /// <param name="before">Mounted rows in display order.</param>
    /// <param name="after">New published rows in display order.</param>
    /// <returns>True when counts and every corresponding row layout match; values are ignored.</returns>
    internal static bool SameLayouts(IReadOnlyList<DeviceOverlayCapability> before,
        IReadOnlyList<DeviceOverlayCapability> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (var index = 0; index < after.Count; index++)
        {
            if (!before[index].SameLayoutAs(after[index]))
            {
                return false;
            }
        }

        return true;
    }
}
