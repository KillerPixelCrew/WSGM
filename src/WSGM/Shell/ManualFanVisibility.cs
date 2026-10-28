using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Shows each fan's controls only in the mode that uses them.</summary>
/// <remarks>
///     A manual speed control appears only while its fan is in Manual: families keep reporting the
///     measured duty through it, so in another mode the slider only mirrors the firmware or the software
///     curve and suggests it can be set. The software curve editor appears only while some fan uses
///     Software (see <see cref="FanControlModes" />). The Overlay and both Steam surfaces share this
///     rule. The mode shown is the desired value when one exists, so a choice reveals its control at
///     once. A fan whose mode is unknown, or whose family has no such choice, keeps its controls.
/// </remarks>
internal static class ManualFanVisibility
{
    /// <summary>Removes fan controls whose mode is not selected.</summary>
    /// <param name="views">The capability snapshot.</param>
    /// <returns>The same list when nothing is hidden, otherwise a filtered copy.</returns>
    internal static IReadOnlyList<DeviceCapabilityView> Filter(IReadOnlyList<DeviceCapabilityView> views)
    {
        var modes = views.Where(view => view.Descriptor.Role == CapabilityRole.FanMode).ToArray();
        if (modes.Length == 0 || !views.Any(view => Hidden(modes, view)))
        {
            return views;
        }

        return [.. views.Where(view => !Hidden(modes, view))];
    }

    /// <summary>Whether a fan control belongs to a mode that is not selected.</summary>
    /// <param name="views">The capability snapshot.</param>
    /// <param name="view">The control to test.</param>
    /// <returns>True for a manual speed outside Manual, or the software curve while no fan uses it.</returns>
    /// <remarks>A restore skips such a value too: writing a saved duty would force the fan into Manual.</remarks>
    internal static bool Hides(IReadOnlyList<DeviceCapabilityView> views, DeviceCapabilityView view)
    {
        return Hidden([.. views.Where(item => item.Descriptor.Role == CapabilityRole.FanMode)], view);
    }

    private static bool Hidden(DeviceCapabilityView[] modes, DeviceCapabilityView view)
    {
        if (view.Descriptor.Role is CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm
            && view.Descriptor.SupportsWrite)
        {
            return !InMode(Mode(modes, view.Descriptor.InstanceId), FanControlModes.Manual);
        }

        // The software curve (instance null) serves every fan in Software; a firmware curve keeps its row.
        return view.Descriptor is { Role: CapabilityRole.FanCurve, InstanceId: null }
               && modes.Any(mode => Offers(mode, FanControlModes.Software))
               && !modes.Any(mode => Offers(mode, FanControlModes.Software) && Current(mode) == FanControlModes.Software);
    }

    private static DeviceCapabilityView? Mode(DeviceCapabilityView[] modes, string? instance)
    {
        return modes.FirstOrDefault(view => view.Descriptor.InstanceId == instance)
               ?? modes.FirstOrDefault(view => view.Descriptor.InstanceId is null);
    }

    private static bool InMode(DeviceCapabilityView? mode, string choice)
    {
        if (mode is null || !Offers(mode, choice))
        {
            return true;
        }

        return Current(mode) is null || Current(mode) == choice;
    }

    private static bool Offers(DeviceCapabilityView mode, string choice)
    {
        return mode.Descriptor.Choices.Any(item => item.Value == choice);
    }

    private static string? Current(DeviceCapabilityView mode)
    {
        return mode.Projection.DesiredValue?.ChoiceValue ?? mode.Projection.State.ObservedValue?.ChoiceValue;
    }
}
