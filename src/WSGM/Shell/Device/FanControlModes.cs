using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Who drives a fan: the firmware, WSGM's software curve, or the manual duty slider.</summary>
/// <remarks>
///     Firmware and Manual are the device's own automatic and custom fan modes. Software is a WSGM
///     choice added to every writable fan mode when the host can drive duty: it is remembered like any
///     other mode and never sent to the firmware. While the curve drives a fan, the firmware reports
///     custom, so that fan's mode is shown as Software instead. Other firmware modes, such as a fixed
///     full speed, stay available beside these three.
/// </remarks>
internal static class FanControlModes
{
    /// <summary>The firmware's automatic mode: WSGM does not control the fan.</summary>
    internal const string Firmware = "automatic";

    /// <summary>WSGM's software curve drives the fan's duty.</summary>
    internal const string Software = "software";

    /// <summary>The firmware's custom mode, set by the manual duty slider.</summary>
    internal const string Manual = "custom";

    /// <summary>Adds the Software choice and names the firmware and manual choices.</summary>
    /// <param name="mode">A writable fan-mode descriptor.</param>
    /// <returns>The descriptor offered to both user surfaces.</returns>
    internal static CapabilityDescriptor WithSoftwareChoice(CapabilityDescriptor mode)
    {
        IEnumerable<CapabilityChoice> choices = mode.Choices.Select(choice => choice.Value switch
        {
            Firmware => choice with { Display = Label("Firmware") },
            Manual => choice with { Display = Label("Manual") },
            _ => choice
        });
        return mode with
        {
            Choices = [.. choices.Where(choice => choice.Value != Software), new CapabilityChoice(Software, Label("Software"))]
        };
    }

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }
}
