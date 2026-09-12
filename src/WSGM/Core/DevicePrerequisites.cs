using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a machine has, of the things a device package needs.</summary>
/// <param name="PackageInstalled">Whether a device package occupies the protected slot.</param>
/// <param name="IntegrationEnabled">Whether Device Integration is switched on.</param>
/// <param name="ControllerLibraryInstalled">Whether the virtual controller library is beside WSGM.</param>
/// <param name="HidHideInstalled">Whether the HidHide control device answers.</param>
public sealed record DevicePrerequisiteState(
    bool PackageInstalled,
    bool IntegrationEnabled,
    bool ControllerLibraryInstalled,
    bool HidHideInstalled);

/// <summary>What the user can do about a package whose prerequisites are missing.</summary>
/// <param name="Detail">One paragraph naming what is missing and what to do, or empty when nothing
/// is.</param>
/// <param name="CanEnableIntegration">Whether offering to switch Device Integration on is useful.</param>
/// <param name="NeedsSetup">Whether the missing half can only be added by re-running setup.</param>
public sealed record DevicePrerequisiteAdvice(string Detail, bool CanEnableIntegration, bool NeedsSetup)
{
    /// <summary>Whether there is anything to tell the user.</summary>
    public bool HasAdvice => Detail.Length > 0;
}

/// <summary>Explains a device package that cannot do its job on this install.
///
/// Setup's Minimal and Desktop modes install no controller support, and a device package can arrive
/// afterwards: the protected slot is a directory an administrator can copy into. That combination
/// is otherwise silent — the package loads, controller management reports itself unavailable, and
/// nothing says why or what would fix it.
///
/// The split between the two halves is not cosmetic. Device Integration is WSGM's own setting and
/// WSGM can turn it on. The virtual controller needs a kernel driver, and INV-020 forbids the
/// runtime from installing one whatever its provenance: the USB/IP install restarts every USB 3.0
/// hub, which drops the built-in controller, the touch digitiser and the keyboard. Underneath a
/// running Game Mode that leaves a person with no input and no way back, so it happens only while
/// setup is on screen.</summary>
public static class DevicePrerequisites
{
    /// <summary>Describes what is missing and which half the user can fix from here.</summary>
    /// <param name="state">What this machine has.</param>
    /// <returns>The advice, which may be empty.</returns>
    public static DevicePrerequisiteAdvice Describe(DevicePrerequisiteState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Nothing is claimed about a machine with no package: an install that never wanted a device
        // is not missing anything, and saying so would be noise on every desktop PC.
        if (!state.PackageInstalled) { return new("", false, false); }

        bool controllerMissing = !state.ControllerLibraryInstalled || !state.HidHideInstalled;
        if (state.IntegrationEnabled && !controllerMissing) { return new("", false, false); }

        List<string> lines = [];
        if (!state.IntegrationEnabled)
        {
            lines.Add("A device package is installed but Device Integration is switched off, "
                + "so none of its controls are active.");
        }
        if (controllerMissing)
        {
            lines.Add(Missing(state)
                + " Controller management stays unavailable until it is added. Re-run the WSGM "
                + "setup and choose the device install mode, or Custom with virtual controller "
                + "support. It installs a driver that restarts USB devices and needs a reboot, "
                + "which is why setup is the only place it can happen.");
        }
        return new(string.Join(" ", lines), !state.IntegrationEnabled, controllerMissing);
    }

    private static string Missing(DevicePrerequisiteState state) =>
        (state.ControllerLibraryInstalled, state.HidHideInstalled) switch
        {
            (false, false) => "This install has neither the virtual controller library nor the "
                + "HidHide driver.",
            (true, false) => "This install has the virtual controller library but not the HidHide "
                + "driver.",
            _ => "This install does not have the virtual controller library.",
        };
}
