using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>Whether the detected handheld and its controller prerequisites are available.</summary>
/// <param name="HandheldSupported">Whether LibHandheld supports the detected hardware.</param>
/// <param name="IntegrationEnabled">Whether Device Integration is switched on.</param>
/// <param name="ControllerLibraryInstalled">Whether the virtual controller library is beside WSGM.</param>
/// <param name="HidHideInstalled">Whether the HidHide control device answers.</param>
/// <param name="RequiredComponents">
///     What the exact handheld definition needs, from <see cref="SetupComponents" />.
/// </param>
/// <param name="PawnIoInstalled">Whether the required PawnIO driver is registered.</param>
/// <param name="InpOutInstalled">Whether the required InpOut driver is registered.</param>
public sealed record DevicePrerequisiteState(
    bool HandheldSupported,
    bool IntegrationEnabled,
    bool ControllerLibraryInstalled,
    bool HidHideInstalled,
    IReadOnlyList<SetupComponent> RequiredComponents,
    bool PawnIoInstalled = true,
    bool InpOutInstalled = true);

/// <summary>What the user can do about an enabled handheld whose prerequisites are missing.</summary>
/// <param name="Detail">
///     One paragraph naming what is missing and what to do, or empty when nothing
///     is.
/// </param>
/// <param name="CanEnableIntegration">Whether offering to switch Device Integration on is useful.</param>
/// <param name="NeedsSetup">Whether the missing half can only be added by re-running setup.</param>
public sealed record DevicePrerequisiteAdvice(string Detail, bool CanEnableIntegration, bool NeedsSetup)
{
    /// <summary>Whether there is anything to tell the user.</summary>
    public bool HasAdvice => Detail.Length > 0;
}

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
