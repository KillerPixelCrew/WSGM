using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>Where a control sits and how WSGM carries it between games.</summary>
/// <param name="SectionId">The declared section.</param>
/// <param name="CategoryId">The section's category, or null.</param>
/// <param name="Order">Placement within the category.</param>
/// <param name="Scope">How WSGM carries a remembered value between games.</param>
/// <param name="Timing">When a written value takes effect.</param>
internal readonly record struct Placement(
    string SectionId,
    string? CategoryId,
    int Order,
    CapabilityProfileScope Scope = CapabilityProfileScope.Switched,
    CapabilityApplyTiming Timing = CapabilityApplyTiming.Immediate);

/// <summary>Builds the package's descriptors in one shape.</summary>
/// <remarks>
///     Every value here is held by the graphics driver, so every descriptor is device persistent: the
///     driver keeps it across a WSGM restart, exactly as it keeps what Intel Graphics Software sets.
/// </remarks>
internal static class Descriptors
{
    public static CapabilityDescriptor Toggle(
        string id,
        string? instance,
        string label,
        Placement placement,
        CapabilityRole role = CapabilityRole.GenericToggle)
    {
        return Base(id, instance, label, placement, role, CapabilityValueKind.Boolean);
    }

    public static CapabilityDescriptor Choice(
        string id,
        string? instance,
        string label,
        IReadOnlyList<(string Value, string Label)> choices,
        Placement placement)
    {
        return Base(id, instance, label, placement, CapabilityRole.GenericChoice, CapabilityValueKind.Choice) with
        {
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(
                    choice.Value,
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(choice.Label) }))
            ]
        };
    }

    public static CapabilityDescriptor Range(
        string id,
        string? instance,
        string label,
        int minimum,
        int maximum,
        int step,
        CapabilityUnit unit,
        Placement placement)
    {
        return Base(id, instance, label, placement, CapabilityRole.GenericRange, CapabilityValueKind.Integer) with
        {
            Minimum = minimum,
            Maximum = maximum,
            Step = step,
            Unit = unit
        };
    }

    /// <summary>Bounds a label to what <see cref="CapabilityDisplay" /> accepts.</summary>
    /// <param name="label">Plain text.</param>
    /// <returns>The label, cut at the limit.</returns>
    public static string Label(string label)
    {
        var trimmed = label.Trim();
        return trimmed.Length <= CapabilityDisplay.MaxCustomLabelLength
            ? trimmed
            : trimmed[..CapabilityDisplay.MaxCustomLabelLength].TrimEnd();
    }

    private static CapabilityDescriptor Base(
        string id,
        string? instance,
        string label,
        Placement placement,
        CapabilityRole role,
        CapabilityValueKind kind)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,
            ValueKind = kind,
            Display = role == CapabilityRole.VariableRefreshRate
                ? new CapabilityDisplay { Key = DisplayKey.VariableRefreshRate }
                : new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(label) },
            SectionId = placement.SectionId,
            CategoryId = placement.CategoryId,
            SortOrder = placement.Order,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = placement.Scope,
            ApplyTiming = placement.Timing
        };
    }
}
