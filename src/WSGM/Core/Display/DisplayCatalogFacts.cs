using System.Collections.Generic;
using System.Runtime.Versioning;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>What discovery learned about one display beyond its current placement.</summary>
/// <param name="Modes">Modes advertised by the driver or monitor EDID.</param>
/// <param name="HdrSupported">Whether the display reported advanced-colour support.</param>
/// <param name="MaximumDpiPercent">Highest scaling percentage it offered, or zero when unknown.</param>
internal sealed record DisplayCatalogFacts(
    IReadOnlyList<DisplayMode> Modes,
    bool HdrSupported,
    int MaximumDpiPercent)
{
    /// <summary>
    ///     Asks one connected display what it advertises, so the answers can be remembered and
    ///     offered again after it is unplugged. Every query is optional: a display that refuses one
    ///     of them still contributes the rest.
    /// </summary>
    /// <param name="target">The connected display to ask.</param>
    /// <returns>What the display advertised.</returns>
    [SupportedOSPlatform("windows")]
    internal static DisplayCatalogFacts Read(DisplayTargetIdentity target)
    {
        var modes = DisplayModes.Read(target)?.Supported ?? ReadEdidModes(target);
        var hdr = DisplayColor.TryReadHdr(target, out _, out var supported) && supported;
        var maximum = DisplayScaling.TryReadRange(target, out _, out _, out var highest) ? highest : 0;
        return new DisplayCatalogFacts(modes, hdr, maximum);
    }

    /// <summary>The monitor's own EDID timings; a display without them still contributes its other facts.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DisplayMode> ReadEdidModes(DisplayTargetIdentity target)
    {
        var edid = DisplayEdid.ReadModes(target);
        if (edid.Status != DisplayEdidStatus.Read)
        {
            Log.Warn($"Display catalog: no EDID modes for '{target.FriendlyName}': {edid.Status}.");
        }

        return edid.Modes;
    }
}
