using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Install;

/// <summary>A system component setup installs on behalf of a plugin.</summary>
public enum SetupComponent
{
    /// <summary>VIIPER, the USB/IP driver and HidHide: the virtual controller and hiding the physical one.</summary>
    ControllerStack
}

/// <summary>
///     Maps the capability roles a plugin declares to the system components it needs. Plugins never
///     name installers or drivers; this table is the only place that knows them.
/// </summary>
public static class SetupComponents
{
    private static readonly IReadOnlyDictionary<CapabilityRole, SetupComponent> ByRole =
        new Dictionary<CapabilityRole, SetupComponent>
        {
            [CapabilityRole.ControllerSource] = SetupComponent.ControllerStack,
            [CapabilityRole.MotionSource] = SetupComponent.ControllerStack,
            [CapabilityRole.HapticSink] = SetupComponent.ControllerStack
        };

    /// <summary>The components a set of declared roles requires, in a stable order.</summary>
    /// <param name="roles">The roles a plugin manifest declares.</param>
    /// <returns>Distinct components, or none.</returns>
    public static IReadOnlyList<SetupComponent> Required(IEnumerable<CapabilityRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return
        [
            .. roles.Where(ByRole.ContainsKey).Select(role => ByRole[role]).Distinct().Order()
        ];
    }

    /// <summary>The roles that bring a component in, for explaining why setup installs it.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The roles, in declaration order of the enum.</returns>
    public static IReadOnlyList<CapabilityRole> Roles(SetupComponent component)
    {
        return [.. ByRole.Where(pair => pair.Value == component).Select(pair => pair.Key).Order()];
    }

    /// <summary>A short, user-facing name.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The name setup and WSGM show.</returns>
    public static string DisplayName(SetupComponent component)
    {
        return component switch
        {
            SetupComponent.ControllerStack => "Virtual controller (VIIPER, USB/IP and HidHide)",
            _ => component.ToString()
        };
    }
}
