using System;
using System.Collections.Generic;
using LibHandheld.Contracts;

namespace WSGM.Install;

/// <summary>A system component setup installs for the selected handheld definition.</summary>
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
///     Maps an exact handheld definition's hardware requirements to optional system components.
/// </summary>
public static class SetupComponents
{
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
