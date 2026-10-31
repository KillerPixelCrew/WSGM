using System;

namespace WSGM.Core;

/// <summary>Classifies one fresh RTSS window against its deadline, and says when a present gap is a hiatus.</summary>
/// <remarks>
///     Pure and allocation-free: every input arrives as an argument. The thresholds are the frame-time
///     rules in <c>docs\autotdp-controller.md</c>; the one utilization rule consulted here, telling a
///     heavy scene from a loading interval, is skipped when no GPU load is published.
/// </remarks>
internal static class AutoTdpWindowClassifier
{
    /// <summary>How far past its deadline a window has to land before it counts as a miss.</summary>
    /// <remarks>
    ///     Not zero tolerance. A frametime mean sits a little above the deadline on a perfectly healthy
    ///     capped game simply because the cap is enforced by sleeping, and raising power at every such
    ///     window would walk straight to maximum and stay there.
    /// </remarks>
    internal const double MissRatio = 1.05;

    /// <summary>A present gap of this many target frames is a hiatus rather than a hitch.</summary>
    /// <remarks>
    ///     Measured in frames so the rule holds at any target: 250 ms at 60 FPS, 500 ms at 30. The
    ///     2026-09-26 captures put ordinary hitches at 95 to 185 ms and stalls at 818 ms and beyond.
    /// </remarks>
    internal const int HiatusFrames = 15;

    /// <summary>A window this far past its deadline is a stall, not a slow frame.</summary>
    internal const double SevereRatio = 1.5;

    /// <summary>At or above this GPU load a stall may be real work after all.</summary>
    internal const double StallPowerBoundGpu = 60;

    /// <summary>How comfortably a window has to beat its deadline before it counts as headroom.</summary>
    private const double ComfortRatio = 0.92;

    /// <summary>The lower edge of the band a limiter holds a healthy game in.</summary>
    private const double CappedRatio = 0.97;

    /// <summary>RTSS closes a window on a frame boundary after this long.</summary>
    private const double NominalWindowMs = 1000;

    /// <summary>A window this much longer than nominal ended with one very long frame.</summary>
    private const double SevereDurationMs = NominalWindowMs * 1.4;

    /// <summary>The shortest gap that can count as a hiatus, whatever the target.</summary>
    private const double MinimumHiatusMs = 250;

    /// <summary>Whether no frame has been presented for long enough to be a hiatus in progress.</summary>
    /// <param name="gapMs">Time since the last present, on the caller's clock.</param>
    /// <param name="observation">The tick, whose deadline sets the threshold.</param>
    /// <returns>True past the nominal window length plus the hiatus threshold, when the deadline is usable.</returns>
    internal static bool IsHiatus(double gapMs, AutoTdpObservation observation)
    {
        return observation.HasTarget && gapMs >= NominalWindowMs + HiatusMs(observation);
    }

    /// <summary>Classifies one fresh, measured window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="observation">The tick carrying the deadline and the GPU load.</param>
    /// <param name="ratio">Receives the mean frametime over the deadline.</param>
    /// <param name="heavyScene">Receives whether GPU load turned a stall-sized ratio into a miss.</param>
    /// <returns>The class.</returns>
    internal static AutoTdpWindowClass Classify(
        AutoTdpWindow window,
        AutoTdpObservation observation,
        out double ratio,
        out bool heavyScene)
    {
        ratio = window.MeanFrametimeMs / observation.TargetFrametimeMs;
        heavyScene = false;
        var hiatus = HiatusMs(observation);
        if (window.LastFrameMs >= hiatus || window.DurationMs >= SevereDurationMs)
        {
            // Three detectors for one condition, and the captures need all three: a blocked render
            // thread that is still blocked shows up as the gap, one that unblocked at the end of the
            // window shows up as the last frame, and a loading interval of many slow frames shows up
            // only in the ratio.
            return AutoTdpWindowClass.Severe;
        }

        if (ratio >= SevereRatio)
        {
            // Many slow frames and no long one. A loading interval looks like this, and so does a scene
            // far heavier than the limit allows; the GPU tells them apart. Entering the late-game
            // village at 8 W gave 24 to 28 frames a window at 73 to 87 % GPU, and calling that a stall
            // quarantined the very raise it was asking for (Claw, 2026-09-27).
            if (observation.GpuLoadPercent >= StallPowerBoundGpu)
            {
                heavyScene = true;
                return AutoTdpWindowClass.Missed;
            }

            return AutoTdpWindowClass.Severe;
        }

        if (ratio > MissRatio)
        {
            return AutoTdpWindowClass.Missed;
        }

        return ratio <= ComfortRatio || ratio >= CappedRatio
            ? AutoTdpWindowClass.Comfortable
            : AutoTdpWindowClass.OnTarget;
    }

    /// <summary>The gap that separates a hitch from a hiatus at this target.</summary>
    private static double HiatusMs(AutoTdpObservation observation)
    {
        return Math.Max(MinimumHiatusMs, HiatusFrames * observation.TargetFrametimeMs);
    }
}
