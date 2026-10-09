using System;
using System.Collections.Generic;
using System.Linq;
using LibHandheld.Contracts;
using CapabilityRole = WSGM.Device.Sdk.Capabilities.CapabilityRole;

namespace WSGM.Install;

/// <summary>A system component setup installs on behalf of a plugin.</summary>
public enum SetupComponent
{
    /// <summary>VIIPER, the USB/IP driver and HidHide: the virtual controller and hiding the physical one.</summary>
    ControllerStack,

    /// <summary>Signed PawnIO driver access for native embedded-controller and CPU interfaces.</summary>
    PawnIo,

    /// <summary>Steam Deck firmware port and MMIO access.</summary>
    InpOut
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

    /// <summary>Native dependencies required by the detected model, independently of common plugins.</summary>
    /// <param name="definition">Exact library model.</param>
    /// <returns>Distinct system components in installation order.</returns>
    public static IReadOnlyList<SetupComponent> Required(HandheldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<SetupComponent> result = [];
        if (definition.HasController)
        {
            result.Add(SetupComponent.ControllerStack);
        }

        if ((definition.HardwareAccess & DeviceHardwareAccess.PawnIo) != 0)
        {
            result.Add(SetupComponent.PawnIo);
        }

        if ((definition.HardwareAccess & DeviceHardwareAccess.InpOut) != 0)
        {
            result.Add(SetupComponent.InpOut);
        }

        return result.AsReadOnly();
    }

    /// <summary>A short, user-facing name.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The name setup and WSGM show.</returns>
    public static string DisplayName(SetupComponent component)
    {
        return component switch
        {
            SetupComponent.ControllerStack => "Virtual controller (VIIPER, USB/IP and HidHide)",
            SetupComponent.PawnIo => "PawnIO handheld hardware access",
            SetupComponent.InpOut => "Steam Deck firmware access (InpOut)",
            _ => component.ToString()
        };
    }
}
