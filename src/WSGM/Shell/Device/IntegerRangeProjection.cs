using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     One writable integer control of a unique role as WSGM tracks it: availability, range, and the raw
///     desired and observed values, for the power limits, the charge limit and the lighting brightness.
/// </summary>
/// <param name="Available">Whether the control is compatible and can be commanded now.</param>
/// <param name="Minimum">The descriptor's lowest value, or null when incompatible.</param>
/// <param name="Maximum">The descriptor's highest value, or null when incompatible.</param>
/// <param name="Step">The descriptor's step, or null when incompatible.</param>
/// <param name="Desired">What a profile asks for, on the descriptor's range, or null.</param>
/// <param name="Observed">What was last read or written, on the descriptor's range, or null.</param>
/// <param name="Progress">The word Steam shows for the command in flight.</param>
/// <param name="StatusText">The bounded status line.</param>
/// <param name="InstanceId">The capability instance a command goes to, or null.</param>
/// <param name="CapabilityId">The capability a command goes to, or null when none was found.</param>
/// <param name="GameOverride">Whether the running game's profile supplies the desired value.</param>
/// <remarks>
///     Neither value is ever filled from the ceiling. A device that cannot read its limits and has no
///     desired value reports neither, so the overlay shows no figure rather than the maximum; only Steam's
///     sliders fall back to a bound to have a position at all.
/// </remarks>
internal sealed record IntegerRangeProjection(
    bool Available,
    int? Minimum,
    int? Maximum,
    int? Step,
    int? Desired,
    int? Observed,
    string Progress,
    string StatusText,
    string? InstanceId,
    string? CapabilityId,
    bool GameOverride = false)
{
    private static readonly RangeText PowerLimitText = new(
        "The active device does not publish a requested power limit.",
        "The active device published an ambiguous requested power limit.",
        "The requested power-limit descriptor is incompatible.",
        "The requested power limit is not currently available.",
        "The desired power limit is outside the current descriptor.");

    /// <summary>Projects the one power limit of a role from the device's capability views.</summary>
    /// <param name="views">Every capability the device publishes.</param>
    /// <param name="role">Which power limit to project.</param>
    /// <returns>The projection, unavailable when the role is missing, ambiguous or incompatible.</returns>
    internal static IntegerRangeProjection PowerLimit(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role = CapabilityRole.PowerSustainedLimit)
    {
        return Project(views, role, CapabilityUnit.Watt, 1, int.MaxValue, PowerLimitText);
    }

    /// <summary>Projects the one percentage control of a role, such as the charge limit or lighting brightness.</summary>
    /// <param name="views">Every capability the device publishes.</param>
    /// <param name="role">Which control to project.</param>
    /// <returns>The projection, unavailable when the role is missing, ambiguous or incompatible.</returns>
    internal static IntegerRangeProjection Percent(IReadOnlyList<DeviceCapabilityView> views, CapabilityRole role)
    {
        var incompatible = Incompatible(role);
        return Project(views, role, CapabilityUnit.Percent, 0, 100, new RangeText(incompatible, incompatible,
            incompatible, "The device control is not currently available.",
            "The desired value is outside the current descriptor."));
    }

    /// <summary>What Steam's percentage slider says when its control cannot be used.</summary>
    /// <param name="role">The control's role.</param>
    /// <returns>The status line.</returns>
    internal static string Incompatible(CapabilityRole role)
    {
        return $"The device published an incompatible or ambiguous {role} control.";
    }

    // The unnamed instance is the primary control of its role; without one, every instance competes.
    private static IntegerRangeProjection Project(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role,
        CapabilityUnit unit,
        int minimumFloor,
        int maximumCeiling,
        RangeText text)
    {
        var all = views.Where(view => view.Descriptor.Role == role).ToArray();
        var primary = all.Where(view => view.Descriptor.InstanceId is null).ToArray();
        var matches = primary.Length > 0 ? primary : all;
        if (matches.Length != 1)
        {
            return Unavailable(matches.Length == 0 ? text.Missing : text.Ambiguous);
        }

        var view = matches[0];
        var descriptor = view.Descriptor;
        var projection = view.Projection;
        var state = projection.State;
        if (descriptor.ValueKind is not CapabilityValueKind.Integer
            || descriptor.Unit != unit
            || !descriptor.SupportsWrite
            || descriptor.Minimum is not { } minimum
            || descriptor.Maximum is not { } maximum
            || descriptor.Step is not { } step
            || minimum < minimumFloor
            || maximum > maximumCeiling
            || minimum >= maximum
            || step < 1
            || step > maximum - minimum)
        {
            return Unavailable(text.Incompatible, descriptor.InstanceId, descriptor.CapabilityId);
        }

        // Readback is not required: firmware that cannot report its value is still commanded.
        var available = CapabilityFreshness.CanCommand(state);
        return new IntegerRangeProjection(
            available,
            minimum,
            maximum,
            step,
            CapabilityProjection.ValidInteger(projection.DesiredValue, minimum, maximum, step),
            CapabilityProjection.ValidInteger(state.ObservedValue, minimum, maximum, step),
            NativeQamUi.ProgressText(projection.Progress),
            NativeQamUi.StatusText(view, available, text.Unavailable, text.OutOfRange),
            descriptor.InstanceId,
            descriptor.CapabilityId,
            CapabilityProjection.DeviceOverrideId(view) is not null);
    }

    private static IntegerRangeProjection Unavailable(
        string detail,
        string? instanceId = null,
        string? capabilityId = null)
    {
        return new IntegerRangeProjection(
            false,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            NativeQamUi.Text(detail),
            instanceId,
            capabilityId);
    }

    /// <summary>The status lines of one kind of control.</summary>
    private sealed record RangeText(
        string Missing,
        string Ambiguous,
        string Incompatible,
        string Unavailable,
        string OutOfRange);
}
