namespace WSGM.Core;

/// <summary>The evidence one chain of raises is judged on: its baseline, its judged windows and its unanswered steps.</summary>
/// <remarks>
///     Each raise is judged over two fresh windows against the ratio the chain started from. A step
///     counts as answered when the ratio improved enough, or when the GPU is busy enough that the step
///     is plainly being turned into work. Single-threaded, like the controller that owns it.
/// </remarks>
internal sealed class AutoTdpRaiseChain
{
    /// <summary>Fresh windows each step of a raise chain is judged over.</summary>
    private const int JudgeWindows = 2;

    /// <summary>How much the ratio must fall for a step to count as answered.</summary>
    private const double Improvement = 0.04;

    /// <summary>Unanswered steps that end a raise chain.</summary>
    private const int UnimprovedSteps = 3;

    /// <summary>At or above this GPU load a step counts as answered however the ratio moved.</summary>
    private const double ResponsiveGpu = 85;

    private int _judged;
    private double? _judgedGpu;
    private double _ratio0;
    private double _ratio1;

    /// <summary>The ratio the chain is judged against, or NaN when no chain is running.</summary>
    internal double Baseline { get; private set; } = double.NaN;

    /// <summary>Consecutive steps delivery did not answer.</summary>
    internal int Unimproved { get; private set; }

    /// <summary>Drops the chain: no baseline, no judged windows, no unanswered steps.</summary>
    internal void Reset()
    {
        _judged = 0;
        _judgedGpu = null;
        Unimproved = 0;
        Baseline = double.NaN;
    }

    /// <summary>Starts a chain from a known ratio, keeping any windows already judged.</summary>
    /// <param name="baseline">The ratio the first raise answers.</param>
    internal void Restart(double baseline)
    {
        Baseline = baseline;
        Unimproved = 0;
    }

    /// <summary>Gives the chain a baseline when it has none yet.</summary>
    /// <param name="baseline">The ratio the raise about to be written answers.</param>
    internal void StartIfIdle(double baseline)
    {
        if (double.IsNaN(Baseline))
        {
            Baseline = baseline;
        }
    }

    /// <summary>Adds one judged window and, once enough are in, reports their verdict.</summary>
    /// <param name="ratio">The window's frametime over deadline.</param>
    /// <param name="gpu">The window's GPU load, or null.</param>
    /// <param name="level">Receives the mean ratio of the judged windows once judged.</param>
    /// <param name="responsiveGpu">Receives whether the GPU was busy enough to count the step as answered.</param>
    /// <returns>True when the step has been judged; the judged windows then start over.</returns>
    internal bool TryJudge(double ratio, double? gpu, out double level, out bool responsiveGpu)
    {
        _ratio1 = _ratio0;
        _ratio0 = ratio;
        _judged++;
        if (gpu is { } load && (_judgedGpu is null || load > _judgedGpu))
        {
            _judgedGpu = load;
        }

        if (_judged < JudgeWindows)
        {
            level = double.NaN;
            responsiveGpu = false;
            return false;
        }

        level = (_ratio0 + _ratio1) / 2;
        responsiveGpu = _judgedGpu >= ResponsiveGpu;
        _judged = 0;
        _judgedGpu = null;
        return true;
    }

    /// <summary>Whether the judged level answered the last step.</summary>
    /// <param name="level">The judged mean ratio.</param>
    /// <param name="responsiveGpu">Whether the GPU was busy enough to count it as answered anyway.</param>
    /// <returns>True when the ratio fell far enough below the baseline, or the GPU says so.</returns>
    internal bool IsAnswered(double level, bool responsiveGpu)
    {
        return level <= Baseline - Improvement || responsiveGpu;
    }

    /// <summary>Records an answered step: the judged level becomes the new baseline.</summary>
    /// <param name="level">The judged mean ratio.</param>
    internal void Answered(double level)
    {
        Unimproved = 0;
        Baseline = level;
    }

    /// <summary>Records an unanswered step.</summary>
    /// <param name="level">The judged mean ratio.</param>
    /// <returns>True while the chain may take another single step; false once it has ended.</returns>
    internal bool Unanswered(double level)
    {
        Unimproved++;
        if (Unimproved >= UnimprovedSteps)
        {
            return false;
        }

        Baseline = level;
        return true;
    }
}
