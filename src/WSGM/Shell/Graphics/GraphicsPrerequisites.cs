using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Graphics settings a driver is given together with another one, and the order they are written in.</summary>
/// <remarks>
///     The driver declares the pairing as <see cref="CapabilityDescriptor.Prerequisite" />; LibGPUDriverInteract
///     names Radeon Anti-Lag for AMD Fluid Motion Frames. This is WSGM's policy for honouring it: the
///     prerequisite goes first, both from a user's edit and in a profile pass, and stays on when the
///     dependent setting is turned off again.
/// </remarks>
internal static class GraphicsPrerequisites
{
    /// <summary>The value a prerequisite is written with.</summary>
    internal static CapabilityValue On => CapabilityValue.Boolean(true);

    /// <summary>The capability that must be on before this value is written, or null.</summary>
    /// <param name="view">The capability the user is changing.</param>
    /// <param name="value">The value the user set.</param>
    /// <returns>The declared prerequisite when the capability is being turned on; otherwise null.</returns>
    internal static string? Required(DeviceCapabilityView view, CapabilityValue value)
    {
        return value.BooleanValue == true ? view.Descriptor.Prerequisite : null;
    }

    /// <summary>Orders a profile pass so a prerequisite is written before what depends on it.</summary>
    /// <param name="views">Every capability the pass considers.</param>
    /// <returns>A priority that is lower for a capability another one names as its prerequisite.</returns>
    internal static Func<DeviceCapabilityView, int> Priority(IEnumerable<DeviceCapabilityView> views)
    {
        HashSet<string> prerequisites =
            new(views.Select(view => view.Descriptor.Prerequisite).OfType<string>(), StringComparer.Ordinal);
        return view => prerequisites.Contains(view.Descriptor.CapabilityId) ? -1 : 0;
    }
}
