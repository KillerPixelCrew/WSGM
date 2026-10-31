using System.Numerics;

namespace WSGM.Core;

/// <summary>The evidence one downward probe is judged on: its windows, its misses and the load it started from.</summary>
/// <remarks>
///     A probe passes after <see cref="Windows" /> judged windows and fails on two misses among the
///     last three. Whether a failure is held against the step is a utilization question answered by
///     <see cref="IsFailureConfirmed" />. Single-threaded, like the controller that owns it.
/// </remarks>
internal sealed class AutoTdpProbeTrial
{
    /// <summary>Windows a downward probe must pass before it is accepted.</summary>
    /// <remarks>Three, the same span the failure test looks back over, so a probe is judged once.</remarks>
    internal const int Windows = 3;

    /// <summary>Judged probe windows the failure test looks back over.</summary>
    private const int FailureWindows = 3;

    /// <summary>Missed windows within that look-back that fail a probe.</summary>
    private const int FailureMisses = 2;

    /// <summary>At or above this GPU load during a missed window a failure is power-correlated.</summary>
    /// <remarks>The same level below which a sustained miss is deferred rather than raised.</remarks>
    private const double LoadedGpu = AutoTdpController.DeferralGpu;

    /// <summary>How far GPU load must climb across a probe for its failure to be power-correlated.</summary>
    private const double GpuRise = 15;

    private int _missHistory;
    private double? _missGpu;

    /// <summary>Windows the current probe has passed.</summary>
    internal int Judged { get; private set; }

    /// <summary>Mean GPU load over the windows that earned the probe, or null.</summary>
    internal double? GpuBefore { get; private set; }

    /// <summary>Mean CPU load over the windows that earned the probe, or null.</summary>
    internal double? CpuBefore { get; private set; }

    /// <summary>Whether the probe has missed often enough to fail.</summary>
    internal bool HasFailed => BitOperations.PopCount((uint)_missHistory) >= FailureMisses;

    /// <summary>Whether the probe has passed every window it needs.</summary>
    internal bool IsComplete => Judged >= Windows;

    /// <summary>Starts judging a probe against the load that earned it.</summary>
    /// <param name="gpuBefore">GPU load before the step, or null.</param>
    /// <param name="cpuBefore">CPU load before the step, or null.</param>
    internal void Begin(double? gpuBefore, double? cpuBefore)
    {
        Judged = 0;
        _missHistory = 0;
        _missGpu = null;
        GpuBefore = gpuBefore;
        CpuBefore = cpuBefore;
    }

    /// <summary>Drops every judgement and the load baseline.</summary>
    internal void Reset()
    {
        Judged = 0;
        _missHistory = 0;
        GpuBefore = null;
        CpuBefore = null;
        _missGpu = null;
    }

    /// <summary>Adds one judged window.</summary>
    /// <param name="missed">Whether the window missed its deadline.</param>
    /// <param name="gpu">The window's GPU load, or null.</param>
    internal void Record(bool missed, double? gpu)
    {
        _missHistory = ((_missHistory << 1) | (missed ? 1 : 0)) & ((1 << FailureWindows) - 1);
        Judged++;
        if (missed && gpu is { } load && (_missGpu is null || load > _missGpu))
        {
            _missGpu = load;
        }
    }

    /// <summary>Whether a failed probe's misses actually correlate with the power that was removed.</summary>
    /// <param name="utilization">Receives the utilization rule that decided, or null when no sensor published.</param>
    /// <returns>True when the failure is held against the step.</returns>
    internal bool IsFailureConfirmed(out string? utilization)
    {
        if (_missGpu is not { } gpu)
        {
            // No sensor. Frame delivery is the only evidence there is, and it says the step hurt.
            utilization = null;
            return true;
        }

        if (gpu >= LoadedGpu)
        {
            utilization = "probe-failed-loaded";
            return true;
        }

        if (GpuBefore is { } before && gpu - before >= GpuRise)
        {
            utilization = "probe-failed-gpu-rose";
            return true;
        }

        // Frames went late while the GPU stayed idle, and it did not climb when the power came off.
        // Nothing here says the watts were the problem, so nothing is held against this step.
        utilization = "probe-inconclusive-low-gpu";
        return false;
    }
}
