namespace WSGM.Core;

/// <summary>Everything the controller is given for one control tick.</summary>
/// <param name="ElapsedMs">Monotonic clock supplied by the caller, in milliseconds.</param>
/// <param name="ContextKey">Application plus target context, or null to keep the current one.</param>
/// <param name="TargetFrametimeMs">The deadline this tick is judged against.</param>
/// <param name="Window">The newest RTSS window, or null when nothing is rendering.</param>
/// <param name="GpuLoadPercent">GPU load, or null when no sensor provider is publishing.</param>
/// <param name="CpuLoadPercent">Total CPU utilization percent, or null; its change across probes can limit probe growth.</param>
/// <remarks>
///     A tick with no window still reaches the controller. RTSS drops an application's entry once it
///     is two seconds stale, so a long enough stall makes the renderer disappear entirely — and a
///     quarantine that ended there would hand the post-loading frames straight back to the estimator.
/// </remarks>
internal sealed record AutoTdpObservation(
    double ElapsedMs,
    string? ContextKey,
    double TargetFrametimeMs,
    AutoTdpWindow? Window,
    double? GpuLoadPercent = null,
    double? CpuLoadPercent = null)
{
    /// <summary>Whether the deadline itself is usable.</summary>
    internal bool HasTarget => double.IsFinite(TargetFrametimeMs) && TargetFrametimeMs > 0;
}
