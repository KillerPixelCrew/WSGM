using System.Collections.Generic;
using LibHandheld.Contracts;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>The Windows side of the extended VRR range: the panel's EDID, its override and the standalone tool.</summary>
/// <remarks>Every call is synchronous registry, topology or file work; the owner calls it off the UI thread.</remarks>
internal interface IExtendedVrrMachine
{
    /// <summary>The active displays whose reported EDID names the panel.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>Every match; the owner refuses anything but exactly one.</returns>
    IReadOnlyList<ExtendedVrrDisplay> FindPanels(ExtendedVrrPanel panel);

    /// <summary>Reads the display's <c>EDID_OVERRIDE</c> key.</summary>
    /// <param name="target">The display.</param>
    /// <returns>What it holds.</returns>
    DisplayEdidOverride ReadOverride(DisplayTargetIdentity target);

    /// <summary>Writes the display's EDID override.</summary>
    /// <param name="target">The display.</param>
    /// <param name="edid">The overriding EDID.</param>
    void WriteOverride(DisplayTargetIdentity target, byte[] edid);

    /// <summary>Removes the display's EDID override.</summary>
    /// <param name="target">The display.</param>
    /// <returns>Whether one was removed.</returns>
    bool RemoveOverride(DisplayTargetIdentity target);

    /// <summary>The standalone Atlas8Tweaks VRR Fix install, or null when none is present.</summary>
    ExtendedVrrStandalone? ReadStandalone();

    /// <summary>Removes the standalone tool's sign-in task and runtime folder.</summary>
    /// <returns>Null when both are gone, otherwise what could not be removed.</returns>
    string? RemoveStandalone();
}
