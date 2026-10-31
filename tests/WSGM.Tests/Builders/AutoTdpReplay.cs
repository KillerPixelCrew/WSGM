using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>Builds observation runs and replays them through the controller.</summary>
/// <remarks>
///     The regression harness for this feature. A reported oscillation or a limit that walked to maximum
///     is reproduced by recording the trace and replaying it here, with no device involved; the
///     controller is only allowed to grow more sophisticated when a recorded trace defeats the simple
///     one. It lives in the test project because nothing in the application replays a trace.
/// </remarks>
internal static class AutoTdpReplay
{
    /// <summary>The window length RTSS produces at a one-second averaging interval.</summary>
    internal const double WindowMs = 1016;

    /// <summary>Runs a trace and returns every decision in order.</summary>
    /// <param name="controller">The controller under test.</param>
    /// <param name="limits">The device bounds each observation is judged against.</param>
    /// <param name="trace">The observations, in order.</param>
    /// <returns>One decision per observation.</returns>
    internal static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        AutoTdpLimits limits,
        IEnumerable<AutoTdpObservation> trace)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(trace);
        return [.. trace.Select(observation => controller.Observe(observation, limits))];
    }

    /// <summary>Builds a run of identical windows, each one a fresh measurement.</summary>
    /// <param name="count">How many windows.</param>
    /// <param name="frametimeMs">Their mean frametime.</param>
    /// <param name="targetFrametimeMs">The deadline they are judged against.</param>
    /// <param name="contextKey">The context they belong to.</param>
    /// <param name="gpuLoadPercent">GPU load to report, or null for no sensor.</param>
    /// <returns>The observations.</returns>
    internal static IEnumerable<AutoTdpObservation> Run(
        int count,
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        double? gpuLoadPercent = null)
    {
        for (var index = 0; index < count; index++)
        {
            yield return Window(frametimeMs, targetFrametimeMs, contextKey, gpuLoadPercent);
        }
    }

    /// <summary>Builds one fresh window on the shared clock.</summary>
    /// <param name="frametimeMs">Its mean frametime.</param>
    /// <param name="targetFrametimeMs">The deadline it is judged against.</param>
    /// <param name="contextKey">The context it belongs to.</param>
    /// <param name="gpuLoadPercent">GPU load to report, or null for no sensor.</param>
    /// <param name="durationMs">How long the window covered.</param>
    /// <param name="lastFrameMs">The window's final frame, or null to derive it from the mean.</param>
    /// <returns>The observation.</returns>
    internal static AutoTdpObservation Window(
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        double? gpuLoadPercent = null,
        double durationMs = WindowMs,
        double? lastFrameMs = null)
    {
        var start = Clock.NextWindowStart(durationMs);
        return new AutoTdpObservation(
            Clock.ElapsedMs,
            contextKey,
            targetFrametimeMs,
            new AutoTdpWindow(
                start,
                start + (uint)durationMs,
                Math.Max(1, (uint)(durationMs / frametimeMs)),
                frametimeMs,
                lastFrameMs ?? frametimeMs,
                Clock.AgeMs),
            gpuLoadPercent);
    }

    /// <summary>Builds a tick that read the same window as the one before it.</summary>
    /// <param name="previous">The observation whose window is being read again.</param>
    /// <param name="ageMs">How long ago that window ended, as of this read.</param>
    /// <returns>The observation.</returns>
    /// <remarks>
    ///     The tick is placed so the age is true: a window that ended at one instant cannot be both
    ///     1032 ms and 1300 ms old at the same moment, and a builder that emitted such a pair would
    ///     be testing arithmetic the real reader never produces.
    /// </remarks>
    internal static AutoTdpObservation Repeat(AutoTdpObservation previous, double ageMs)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var window = previous.Window!;
        var elapsed = previous.ElapsedMs - window.AgeMs + ageMs;
        Clock.AdvanceTo(elapsed);
        return previous with
        {
            ElapsedMs = elapsed,
            Window = window with { AgeMs = ageMs }
        };
    }

    /// <summary>Builds a tick with no renderer at all.</summary>
    /// <param name="contextKey">The context that is still running.</param>
    /// <param name="targetFrametimeMs">The deadline still in force.</param>
    /// <returns>The observation.</returns>
    internal static AutoTdpObservation Silent(string contextKey, double targetFrametimeMs)
    {
        Clock.Advance();
        return new AutoTdpObservation(Clock.ElapsedMs, contextKey, targetFrametimeMs, null);
    }

    /// <summary>Restarts the shared clock, so one test's windows never follow another's.</summary>
    internal static void ResetClock()
    {
        Clock.Reset();
    }

    /// <summary>
    ///     The tick clock and RTSS tick counter the builders share.
    /// </summary>
    /// <remarks>
    ///     Window identity and elapsed time are both inputs now, so a builder cannot emit constants:
    ///     two windows with the same bounds are one measurement read twice, and a controller whose
    ///     dwells are sums of window time never advances on a frozen clock. Test parallelization is
    ///     off in this project, which is what lets this be static.
    /// </remarks>
    private static class Clock
    {
        private const uint FirstTick = 100_000;
        private static uint _tick = FirstTick;

        internal static double ElapsedMs { get; private set; }

        /// <summary>How long ago the newest window ended, as the service would have read it.</summary>
        internal static double AgeMs => 16;

        internal static void Reset()
        {
            _tick = FirstTick;
            ElapsedMs = 0;
        }

        internal static void Advance()
        {
            ElapsedMs += WindowMs;
        }

        internal static void AdvanceTo(double elapsedMs)
        {
            ElapsedMs = Math.Max(ElapsedMs, elapsedMs);
        }

        internal static uint NextWindowStart(double durationMs)
        {
            var start = _tick;
            _tick += (uint)durationMs;
            ElapsedMs += durationMs;
            return start;
        }
    }
}
