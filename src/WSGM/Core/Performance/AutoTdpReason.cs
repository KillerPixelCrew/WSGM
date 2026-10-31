namespace WSGM.Core;

/// <summary>Turns a controller reason token into something worth showing a user.</summary>
/// <remarks>
///     The overlay's Device page and Steam's Quick Access row both print the controller's detail
///     verbatim, so before this existed a handheld's power row read "below-learned-floor". The token
///     itself stays in the log and the trace, where a stable identifier is what is wanted.
/// </remarks>
internal static class AutoTdpReason
{
    /// <summary>The short phrase for a reason token.</summary>
    /// <param name="reason">The token from <see cref="AutoTdpDecision.Reason" />.</param>
    /// <returns>A phrase for a user-facing row.</returns>
    internal static string Describe(string? reason)
    {
        return reason switch
        {
            "stopped" => "Stopped",
            "paused-manual" => "Paused; the limit was set by hand",
            "limits-unusable" => "The power limit reports no usable range",
            "no-telemetry" => "Waiting for frame data",
            "window-repeat" => "Waiting for a new measurement",
            "context-changed" => "Starting over for this game",
            "settling" => "Letting the new limit settle",
            "on-target" => "Meeting the target with nothing to spare",
            "tracking-headroom" => "Watching for spare power",
            "miss-unconfirmed" => "Checking whether frames are really late",
            "miss-deferred" => "Frames are late but the GPU is idle",
            "sustained-miss" => "Adding power",
            "raise-pending" => "Checking whether the extra power helped",
            "raise-resolved" => "Holding at the power the game needs",
            "unresponsive" => "More power is not helping; holding",
            "unresponsive-ended" => "Watching for spare power",
            "quarantine-hiatus" => "Waiting out a stall",
            "quarantine-stall" => "Waiting out a stall",
            "quarantine-recovery" => "Waiting for frames to steady",
            "quarantine-ended" => "Watching for spare power",
            "probe-down" => "Testing a lower limit",
            "probe-pending" => "Testing a lower limit",
            "probe-accepted" => "Settled at a lower limit",
            "probe-failed" => "The lower limit cost frames",
            "probe-inconclusive" => "The lower test was inconclusive",
            "probe-interrupted" => "A stall interrupted the test",
            "at-minimum" => "At the device minimum",
            "at-maximum" => "At the device maximum",
            _ => reason ?? string.Empty
        };
    }
}
