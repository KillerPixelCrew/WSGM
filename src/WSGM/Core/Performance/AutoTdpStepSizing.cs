using System;

namespace WSGM.Core;

/// <summary>How many device steps a raise or a downward probe takes.</summary>
/// <remarks>
///     Pure arithmetic over the controller's evidence. Frames size every step; load can only hold a
///     descent's growth back, never make a step larger or size one from its own level.
/// </remarks>
internal static class AutoTdpStepSizing
{
    /// <summary>The most device steps one downward probe may take.</summary>
    internal const int MaximumProbeSteps = 8;

    /// <summary>The largest share of the current limit one downward probe may give up.</summary>
    private const double MaximumProbeFraction = 1.0 / 3;

    /// <summary>A load rise across an accepted probe that stops the next probe from growing.</summary>
    /// <remarks>
    ///     Relative, never a threshold on the load itself: RTSS reports utilization under the current
    ///     power envelope, so 60 % means something different at 28 W than at 11 W. What carries over is
    ///     the change. Load that stayed put while power came off says the step cost nothing; load that
    ///     climbed says the step ate into the headroom, and the next one should be smaller (issue 181).
    /// </remarks>
    private const double ProbeLoadRise = 10;

    /// <summary>How much power a raise adds for each unit of deficit.</summary>
    /// <remarks>
    ///     Frame rate grows more slowly than power, so a raise of exactly the deficit undershoots and
    ///     needs a second one: frames 9 % late at 16 W asked for a 1.4 W raise and three writes (Claw,
    ///     2026-09-27). Overshoot is cheap now that a descent takes it back within seconds.
    /// </remarks>
    private const double RaiseGain = 1.5;

    /// <summary>The largest share of the current limit one raise may add.</summary>
    /// <remarks>A stall's ratio is not a measure of demand, so no single raise more than doubles the limit.</remarks>
    private const double MaximumRaiseFraction = 1.0;

    /// <summary>How many device steps a raise takes for the ratio it answers.</summary>
    /// <param name="watts">The limit in effect.</param>
    /// <param name="ratio">The ratio this raise answers, or NaN for a single step.</param>
    /// <param name="limits">The device bounds.</param>
    /// <returns>At least one step.</returns>
    /// <remarks>
    ///     Sized to the deficit and half again: frames 20 % late ask for 30 % more power, never more
    ///     than doubling the limit. One step per write took five minutes to climb 23 W (Claw,
    ///     2026-09-27).
    /// </remarks>
    internal static int RaiseSteps(int watts, double ratio, AutoTdpLimits limits)
    {
        if (!double.IsFinite(ratio) || ratio <= 1)
        {
            return 1;
        }

        var share = Math.Min((ratio - 1) * RaiseGain, MaximumRaiseFraction);
        // The small allowance keeps a product that lands on a whole step from rounding up a step
        // because of binary fractions.
        return Math.Max(1, (int)Math.Ceiling(watts * share / limits.Step - 1e-9));
    }

    /// <summary>How many device steps the next downward probe takes.</summary>
    /// <param name="watts">The limit in effect.</param>
    /// <param name="descentSteps">The step the descent has grown to.</param>
    /// <param name="failedWatts">A limit this descent already found too low, or null.</param>
    /// <param name="limits">The device bounds.</param>
    /// <returns>The step count, or zero when the descent has reached a limit it found too low.</returns>
    /// <remarks>
    ///     Frames set the pace: one step after a dwell, doubling with each probe they accept, never
    ///     more than a third of the limit. Load can only hold that growth back (see
    ///     <see cref="NextProbeSteps" />), and inside one descent a probe never reaches past half the
    ///     distance to a limit that already failed.
    /// </remarks>
    internal static int ProbeSteps(int watts, int descentSteps, int? failedWatts, AutoTdpLimits limits)
    {
        var cap = Math.Max(1, (int)(watts * MaximumProbeFraction / limits.Step));
        var steps = Math.Min(descentSteps, cap);
        if (failedWatts is not { } failed)
        {
            return steps;
        }

        var gap = (watts - failed) / limits.Step;
        return gap <= 1 ? 0 : Math.Min(steps, gap / 2);
    }

    /// <summary>Whether the busier of GPU and CPU climbed across an accepted probe.</summary>
    /// <param name="gpuNow">Mean GPU load over the probe's windows, or null.</param>
    /// <param name="gpuBefore">Mean GPU load over the windows that earned the probe, or null.</param>
    /// <param name="cpuNow">Mean CPU load over the probe's windows, or null.</param>
    /// <param name="cpuBefore">Mean CPU load over the windows that earned the probe, or null.</param>
    /// <returns>True when either rose by the probe load rise; an absent reading never counts.</returns>
    internal static bool LoadRose(double? gpuNow, double? gpuBefore, double? cpuNow, double? cpuBefore)
    {
        return gpuNow - gpuBefore >= ProbeLoadRise || cpuNow - cpuBefore >= ProbeLoadRise;
    }

    /// <summary>The step a descent takes after an accepted probe.</summary>
    /// <param name="descentSteps">The step the accepted probe took.</param>
    /// <param name="loadRose">Whether load climbed across that probe.</param>
    /// <returns>Half the step when load rose, else double it up to the maximum.</returns>
    /// <remarks>
    ///     Doubles, unless the busier of GPU and CPU climbed across the probe. Load is tertiary evidence
    ///     here, as issue 181 asks: it never sizes a step from its own level and never makes one larger,
    ///     it only notices that taking power away was starting to cost something and slows the descent
    ///     before frames have to prove it. Aggregate CPU load hides a saturated thread, so its rise can
    ///     be missed; that only leaves the step to frames, which is where it was anyway.
    /// </remarks>
    internal static int NextProbeSteps(int descentSteps, bool loadRose)
    {
        return loadRose
            ? Math.Max(1, descentSteps / 2)
            : Math.Min(descentSteps * 2, MaximumProbeSteps);
    }
}
