using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Hides a fan's manual speed controls while that fan is not in its manual mode.</summary>
/// <remarks>
///     Families publish the manual mode as the <c>custom</c> fan-mode choice and keep reporting the
///     measured duty through the speed control, so outside that mode the slider only mirrors what the
///     firmware does and suggests it can be set. The Overlay and both Steam surfaces share this rule. The
///     mode shown is the desired value when one exists, so choosing Manual reveals the slider at once.
///     A fan whose mode is unknown, or whose family has no <c>custom</c> choice, keeps its controls.
/// </remarks>
internal static class ManualFanVisibility
{
    private const string ManualMode = "custom";

    /// <summary>Removes manual speed controls whose fan is in another mode.</summary>
    /// <param name="views">The capability snapshot.</param>
    /// <returns>The same list when nothing is hidden, otherwise a filtered copy.</returns>
    internal static IReadOnlyList<DeviceCapabilityView> Filter(IReadOnlyList<DeviceCapabilityView> views)
    {
        var modes = views.Where(view => view.Descriptor.Role == CapabilityRole.FanMode).ToArray();
        if (modes.Length == 0 || !views.Any(view => IsManualSpeed(view) && !InManualMode(modes, view)))
        {
            return views;
        }

        return [.. views.Where(view => !IsManualSpeed(view) || InManualMode(modes, view))];
    }

    private static bool IsManualSpeed(DeviceCapabilityView view)
    {
        return view.Descriptor.Role is CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm
               && view.Descriptor.SupportsWrite;
    }

    private static bool InManualMode(DeviceCapabilityView[] modes, DeviceCapabilityView speed)
    {
        var mode = modes.FirstOrDefault(view => view.Descriptor.InstanceId == speed.Descriptor.InstanceId)
                   ?? modes.FirstOrDefault(view => view.Descriptor.InstanceId is null);
        if (mode is null || mode.Descriptor.Choices.All(choice => choice.Value != ManualMode))
        {
            return true;
        }

        var current = mode.Projection.DesiredValue?.ChoiceValue ?? mode.Projection.State.ObservedValue?.ChoiceValue;
        return current is null or ManualMode;
    }
}
