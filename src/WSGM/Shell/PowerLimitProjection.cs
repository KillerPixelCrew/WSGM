using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     One device power limit as WSGM tracks it: availability, range, and the raw desired and observed
///     values, for Steam's sliders, AutoTDP and the RTSS overlay alike.
/// </summary>
/// <param name="Available">Whether the limit can be commanded now.</param>
/// <param name="MinimumWatts">The descriptor's lowest value, or null when unavailable.</param>
/// <param name="MaximumWatts">The descriptor's highest value, or null when unavailable.</param>
/// <param name="StepWatts">The descriptor's step, or null when unavailable.</param>
/// <param name="DesiredWatts">What a profile asks for, on the descriptor's range, or null.</param>
/// <param name="ObservedWatts">What was last read or written, on the descriptor's range, or null.</param>
/// <param name="Progress">The word Steam shows for the command in flight.</param>
/// <param name="StatusText">The bounded status line.</param>
/// <param name="InstanceId">The capability instance a command goes to, or null.</param>
/// <param name="CapabilityId">The capability a command goes to, or null when none was found.</param>
/// <remarks>
///     Neither watt value is ever filled from the ceiling. A device that cannot read its limits and
///     has no desired value reports neither, so the overlay shows no figure rather than the maximum;
///     only Steam's slider falls back to the ceiling to have a position at all.
/// </remarks>
internal sealed record PowerLimitProjection(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts,
    int? StepWatts,
    int? DesiredWatts,
    int? ObservedWatts,
    string Progress,
    string StatusText,
    string? InstanceId,
    string? CapabilityId)
{
    /// <summary>Projects the one power limit of a role from the device's capability views.</summary>
    /// <param name="views">Every capability the device publishes.</param>
    /// <param name="role">Which power limit to project.</param>
    /// <returns>The projection, unavailable when the role is missing, ambiguous or incompatible.</returns>
    internal static PowerLimitProjection Project(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role = CapabilityRole.PowerSustainedLimit)
    {
        var matches = views
            .Where(view => view.Descriptor.Role == role)
            .ToArray();
        if (matches.Length != 1)
        {
            return Unavailable(matches.Length == 0
                ? "The active device does not publish a requested power limit."
                : "The active device published an ambiguous requested power limit.");
        }

        var view = matches[0];
        var descriptor = view.Descriptor;
        var projection = view.Projection;
        var state = projection.State;
        if (descriptor.Role != role
            || descriptor.ValueKind is not CapabilityValueKind.Integer
            || descriptor.Unit is not CapabilityUnit.Watt
            || !descriptor.SupportsWrite
            || descriptor.Minimum is not { } minimum
            || descriptor.Maximum is not { } maximum
            || descriptor.Step is not { } step
            || minimum < 1
            || minimum >= maximum
            || step < 1
            || step > maximum - minimum)
        {
            return Unavailable(
                "The requested power-limit descriptor is incompatible.",
                descriptor.InstanceId,
                descriptor.CapabilityId);
        }

        // Readback is not required: firmware that cannot report its limits is still commanded.
        var available = DeviceCapabilityRouter.CanCommand(state);
        return new PowerLimitProjection(
            available,
            minimum,
            maximum,
            step,
            CapabilityProjection.ValidInteger(projection.DesiredValue, minimum, maximum, step),
            CapabilityProjection.ValidInteger(state.ObservedValue, minimum, maximum, step),
            NativeQamUi.ProgressText(projection.Progress),
            NativeQamUi.StatusText(
                view,
                available,
                "The requested power limit is not currently available.",
                "The desired power limit is outside the current descriptor."),
            descriptor.InstanceId,
            descriptor.CapabilityId);
    }

    private static PowerLimitProjection Unavailable(
        string detail,
        string? instanceId = null,
        string? capabilityId = null)
    {
        return new PowerLimitProjection(
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
}
