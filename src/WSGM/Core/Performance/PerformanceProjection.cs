using System;

namespace WSGM.Core;

/// <summary>The RTSS wording and frame-limit bounds the overlay and Steam's Quick Access both show.</summary>
internal static class PerformanceProjection
{
    /// <summary>The highest frame cap either slider offers.</summary>
    /// <remarks>
    ///     280 rather than whatever RTSS reports it will accept (1000). The slider has to be crossable
    ///     on a thumbstick, and a range that reaches a thousand makes every rate anyone actually uses
    ///     live in its first third. This covers every panel a handheld drives, internal or attached.
    /// </remarks>
    internal const int MaximumFrameLimit = 280;

    /// <summary>Why RTSS cannot be used in this state, or null when the probe gives no fixed reason.</summary>
    /// <param name="availability">The probe's availability.</param>
    /// <returns>The sentence for an unavailable state; null for Ready and for states that carry their own diagnostic.</returns>
    internal static string? UnavailableText(RtssAvailability availability)
    {
        return availability switch
        {
            RtssAvailability.Unknown => "Checking RTSS…",
            RtssAvailability.NotInstalled => "RTSS is not installed.",
            RtssAvailability.NotRunning => "RTSS is not running.",
            RtssAvailability.Incompatible => "The installed RTSS version is not supported.",
            RtssAvailability.AdapterUnavailable => "The RTSS profile API is unavailable.",
            _ => null
        };
    }

    /// <summary>Which RTSS profile the controls currently edit.</summary>
    /// <param name="target">The resolved application target, or null for Global.</param>
    /// <returns>The profile line.</returns>
    internal static string TargetText(PerformanceApplicationTarget? target)
    {
        return target switch
        {
            null => "RTSS · global profile",
            { RtssProfileName: { Length: > 0 } profile } => $"RTSS · {profile}",
            { SteamAppId: { } appId } => $"Steam AppID {appId} · executable pending",
            _ => "Foreground application · executable pending"
        };
    }

    /// <summary>The frame-cap bounds a slider offers.</summary>
    /// <param name="capabilities">What this RTSS build accepts.</param>
    /// <param name="panel">The lowest and highest cap the panel's refresh rates suggest, or null when unknown.</param>
    /// <returns>
    ///     The panel's band within what RTSS accepts and <see cref="MaximumFrameLimit" />, with whether it came
    ///     from the panel; without a usable panel band, RTSS's own floor up to the same ceiling.
    /// </returns>
    internal static (int Minimum, int Maximum, bool FromPanel) FrameLimitBounds(RtssCapabilities capabilities,
        (int Minimum, int Maximum)? panel)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var ceiling = Math.Min(MaximumFrameLimit, capabilities.MaximumFrameLimit);
        if (panel is { } band)
        {
            var floor = Math.Max(capabilities.MinimumFrameLimit, band.Minimum);
            var top = Math.Min(band.Maximum, ceiling);
            if (top >= floor)
            {
                return (floor, top, true);
            }
        }

        return (Math.Max(0, capabilities.MinimumFrameLimit), ceiling, false);
    }
}
