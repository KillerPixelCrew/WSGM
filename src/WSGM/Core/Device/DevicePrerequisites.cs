using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>
///     Explains missing dependencies for an enabled exact handheld definition.
///     Setup installs optional controller and hardware components for the selected definition.
///     The virtual controller needs a kernel driver, and INV-020 forbids the
///     runtime from installing one whatever its provenance: the USB/IP install restarts every USB 3.0
///     hub, which drops the built-in controller, the touch digitiser and the keyboard. Underneath a
///     running Game Mode that leaves a person with no input and no way back, so it happens only while
///     setup is on screen.
/// </summary>
public static class DevicePrerequisites
{
    /// <summary>Describes what is missing and which half the user can fix from here.</summary>
    /// <param name="state">What this machine has.</param>
    /// <returns>The advice, which may be empty.</returns>
    public static DevicePrerequisiteAdvice Describe(DevicePrerequisiteState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // A disabled integration has no missing dependency to report.
        if (!state.HandheldSupported || !state.IntegrationEnabled)
        {
            return new DevicePrerequisiteAdvice("", false, false);
        }

        var controllerMissing = state.RequiredComponents.Contains(SetupComponent.ControllerStack)
                                && (!state.ControllerLibraryInstalled || !state.HidHideInstalled);
        var pawnIoMissing = state.RequiredComponents.Contains(SetupComponent.PawnIo) && !state.PawnIoInstalled;
        var inpOutMissing = state.RequiredComponents.Contains(SetupComponent.InpOut) && !state.InpOutInstalled;
        if (!controllerMissing && !pawnIoMissing && !inpOutMissing)
        {
            return new DevicePrerequisiteAdvice("", false, false);
        }

        List<string> lines = [];
        if (controllerMissing)
        {
            lines.Add(Missing(state)
                      + " Controller management stays unavailable until it is added. Run Repair from "
                      + "WSGM Settings, Plugins, or from Windows Settings, Apps: setup installs what the "
                      + "handheld needs. It installs a driver that restarts USB devices and needs a reboot, "
                      + "which is why setup is the only place it can happen.");
        }

        if (pawnIoMissing || inpOutMissing)
        {
            var missing = pawnIoMissing && inpOutMissing ? "PawnIO and InpOut" : pawnIoMissing ? "PawnIO" : "InpOut";
            lines.Add(
                $"{missing} hardware access is missing. Run Setup Repair to add the selected handheld components.");
        }

        return new DevicePrerequisiteAdvice(string.Join(" ", lines), false, true);
    }

    private static string Missing(DevicePrerequisiteState state)
    {
        return (state.ControllerLibraryInstalled, state.HidHideInstalled) switch
        {
            (false, false) => "This install has neither the virtual controller library nor the "
                              + "HidHide driver.",
            (true, false) => "This install has the virtual controller library but not the HidHide "
                             + "driver.",
            _ => "This install does not have the virtual controller library."
        };
    }
}
