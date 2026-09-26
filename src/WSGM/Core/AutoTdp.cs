using System;
using System.Numerics;

namespace WSGM.Core;

/// <summary>What the AutoTDP controller decided to do with the primary power limit.</summary>
internal enum AutoTdpAction
{
    /// <summary>Leave the current limit alone.</summary>
    Hold,

    /// <summary>Raise the limit one step because frames are missing their deadline.</summary>
    Raise,

    /// <summary>Try one step lower because delivery has been comfortable.</summary>
    Probe,

    /// <summary>Go back to the last limit that delivered, because the probe hurt.</summary>
    Restore,

    /// <summary>Hand the limit back to whoever set it manually.</summary>
    Release
}

/// <summary>Where the controller is in its control loop.</summary>
internal enum AutoTdpPhase
{
    /// <summary>Waiting for a write to take effect. Nothing is judged.</summary>
    Settling,

    /// <summary>The steady state: counting evidence toward a raise or a downward probe.</summary>
    Tracking,

    /// <summary>Stepping up, and judging whether delivery responded to each step.</summary>
    Raising,

    /// <summary>Power went up and delivery did not follow. Holding rather than climbing further.</summary>
    Unresponsive,

    /// <summary>A stall or present hiatus is contaminating the evidence. Nothing is judged or learned.</summary>
    Quarantine,

    /// <summary>A step down is in effect and being judged.</summary>
    Probing
}

/// <summary>What one fresh observation window was.</summary>
internal enum AutoTdpWindowClass
{
    /// <summary>No fresh window: the same window read again, or no renderer at all.</summary>
    None,

    /// <summary>A stall, present hiatus or loading interval. Contaminated, never evidence.</summary>
    Severe,

    /// <summary>Delivery below target. Evidence once it is sustained.</summary>
    Missed,

    /// <summary>Meeting the deadline with no headroom to give away.</summary>
    OnTarget,

    /// <summary>Headroom, or the limiter holding the game at its target.</summary>
    Comfortable
}

/// <summary>The plugin-published bounds of the primary power capability.</summary>
/// <param name="Minimum">Lowest limit the device accepts.</param>
/// <param name="Maximum">Highest limit the device accepts.</param>
/// <param name="Step">Smallest change the device accepts.</param>
internal sealed record AutoTdpLimits(int Minimum, int Maximum, int Step)
{
    /// <summary>Whether the bounds describe a usable control.</summary>
    internal bool IsUsable => Step > 0 && Minimum > 0 && Maximum >= Minimum + Step;

    /// <summary>Clamps a candidate limit onto the device grid.</summary>
    /// <param name="value">The candidate limit.</param>
    /// <returns>A limit the device accepts.</returns>
    internal int Clamp(int value)
    {
        return Math.Clamp(value, Minimum, Maximum);
    }
}

/// <summary>One RTSS measurement window, exactly as the shared memory reported it.</summary>
/// <param name="StartTicks">RTSS <c>dwTime0</c>, the tick the window started at.</param>
/// <param name="EndTicks">RTSS <c>dwTime1</c>, the tick it ended at.</param>
/// <param name="Frames">Frames presented inside it.</param>
/// <param name="MeanFrametimeMs">Its mean frametime.</param>
/// <param name="LastFrameMs">The most recent frame's own time, when RTSS reported one.</param>
/// <param name="AgeMs">How long ago the window ended, as of the read.</param>
/// <remarks>
///     The window bounds are its identity. Two reads that return the same bounds read one measurement
///     twice, and the controller must not count it twice; the growing age of a window that will not
///     close is how a blocked render thread announces itself before its own window exists.
/// </remarks>
internal sealed record AutoTdpWindow(
    uint StartTicks,
    uint EndTicks,
    uint Frames,
    double MeanFrametimeMs,
    double? LastFrameMs,
    double AgeMs)
{
    /// <summary>How long the window covered.</summary>
    internal double DurationMs => EndTicks - StartTicks;

    /// <summary>Whether the window has usable numbers at all.</summary>
    internal bool IsMeasured =>
        double.IsFinite(MeanFrametimeMs) && MeanFrametimeMs > 0 && Frames > 0 && EndTicks > StartTicks;
}

/// <summary>Everything the controller is given for one control tick.</summary>
/// <param name="ElapsedMs">Monotonic clock supplied by the caller, in milliseconds.</param>
/// <param name="ContextKey">Application plus target context, or null to keep the current one.</param>
/// <param name="TargetFrametimeMs">The deadline this tick is judged against.</param>
/// <param name="Window">The newest RTSS window, or null when nothing is rendering.</param>
/// <param name="GpuLoadPercent">GPU load, or null when no sensor provider is publishing.</param>
/// <param name="CpuLoadPercent">Total CPU load, recorded for diagnosis and used by no rule.</param>
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

/// <summary>What the controller decided for one observation window.</summary>
/// <param name="Action">The kind of change.</param>
/// <param name="Watts">The limit that should now be in effect.</param>
/// <param name="Reason">Stable diagnostic token, for logs and replay assertions.</param>
internal sealed record AutoTdpDecision(AutoTdpAction Action, int Watts, string Reason)
{
    /// <summary>Whether this decision requires a hardware write.</summary>
    internal bool RequiresWrite => Action is not AutoTdpAction.Hold;
}

/// <summary>A read-only view of the controller's evidence, for the AutoTDP trace.</summary>
/// <param name="ContextKey">The context the controller is judging.</param>
/// <param name="Watts">The limit the controller believes is in effect.</param>
/// <param name="LastGood">The limit in effect before the current probe.</param>
/// <param name="Phase">Where the control loop is.</param>
/// <param name="IsPaused">Whether a manual change suspended control.</param>
/// <param name="Class">What the last window was classified as.</param>
/// <param name="Ratio">Frametime over deadline for the last judged window, or NaN.</param>
/// <param name="GapMs">Time since the last fresh window ended.</param>
/// <param name="Hiatus">Whether a present hiatus is in progress.</param>
/// <param name="MissedWindows">Consecutive missed windows counted toward a raise.</param>
/// <param name="DwellMs">Stable window time accumulated toward a downward probe.</param>
/// <param name="RequiredDwellMs">Stable window time this probe currently needs.</param>
/// <param name="RaiseBaseline">The ratio the current raise chain is judged against, or NaN.</param>
/// <param name="RaiseUnimproved">Steps in this chain that delivery did not answer.</param>
/// <param name="ProbeWindows">Windows the current probe has passed.</param>
/// <param name="SettlingWindows">Windows still ignored after the last write.</param>
/// <param name="SevereWindows">Consecutive severe windows inside a quarantine.</param>
/// <param name="Utilization">Which utilization rule last changed an outcome, or null.</param>
internal readonly record struct AutoTdpControllerSnapshot(
    string ContextKey,
    int Watts,
    int LastGood,
    AutoTdpPhase Phase,
    bool IsPaused,
    AutoTdpWindowClass Class,
    double Ratio,
    double GapMs,
    bool Hiatus,
    int MissedWindows,
    double DwellMs,
    double RequiredDwellMs,
    double RaiseBaseline,
    int RaiseUnimproved,
    int ProbeWindows,
    int SettlingWindows,
    int SevereWindows,
    string? Utilization);

/// <summary>
///     The one deterministic AutoTDP control policy.
/// </summary>
/// <remarks>
///     Delivered frames decide; utilization only ever adjusts confidence in what a frame-time event
///     meant. The four places it is consulted are named in <c>docs\autotdp-controller.md</c>, and each
///     is skipped when no sensor provider is publishing, so the controller degrades to frametime-only
///     rather than behaving differently.
///     <para>
///         Nothing is learned. No floor survives a probe, a context or a session: every conclusion is
///         re-tested from fresh evidence, and the single piece of memory is a bounded backoff that
///         slows repeat probing at one operating point without ever forbidding it. The failed-probe
///         floor this replaced held a handheld at 24 W for 25 minutes of capped 60 FPS play.
///     </para>
///     <para>
///         Pure and single-threaded on purpose. Every input arrives as an argument, every decision is a
///         return value, and the whole controller replays exactly from a recorded trace — which is how a
///         reported oscillation gets reproduced without the hardware that produced it.
///     </para>
/// </remarks>
internal sealed class AutoTdpController
{
    /// <summary>How far past its deadline a window has to land before it counts as a miss.</summary>
    /// <remarks>
    ///     Not zero tolerance. A frametime mean sits a little above the deadline on a perfectly healthy
    ///     capped game simply because the cap is enforced by sleeping, and raising power at every such
    ///     window would walk straight to maximum and stay there.
    /// </remarks>
    internal const double MissRatio = 1.05;

    /// <summary>Consecutive missed windows before power is raised.</summary>
    internal const int SustainedMisses = 3;

    /// <summary>Windows ignored after a write, while the limit takes effect.</summary>
    internal const int SettleWindows = 2;

    /// <summary>Windows a downward probe must pass before it is accepted.</summary>
    internal const int ProbeWindows = 6;

    /// <summary>A present gap of this many target frames is a hiatus rather than a hitch.</summary>
    /// <remarks>
    ///     Measured in frames so the rule holds at any target: 250 ms at 60 FPS, 500 ms at 30. The
    ///     2026-09-26 captures put ordinary hitches at 95 to 185 ms and stalls at 818 ms and beyond.
    /// </remarks>
    internal const int HiatusFrames = 15;

    /// <summary>How comfortably a window has to beat its deadline before it counts as headroom.</summary>
    private const double ComfortRatio = 0.92;

    /// <summary>The lower edge of the band a limiter holds a healthy game in.</summary>
    private const double CappedRatio = 0.97;

    /// <summary>A window this far past its deadline is a stall, not a slow frame.</summary>
    private const double SevereRatio = 1.5;

    /// <summary>RTSS closes a window on a frame boundary after this long.</summary>
    private const double NominalWindowMs = 1000;

    /// <summary>A window this much longer than nominal ended with one very long frame.</summary>
    private const double SevereDurationMs = NominalWindowMs * 1.4;

    /// <summary>The shortest gap that can count as a hiatus, whatever the target.</summary>
    private const double MinimumHiatusMs = 250;

    /// <summary>Window time a write is given before anything is judged again.</summary>
    private const double SettleMs = 2000;

    /// <summary>Stable window time before the first downward probe at an operating point.</summary>
    private const double StabilityDwellMs = 10_000;

    /// <summary>The longest the backoff may stretch that dwell.</summary>
    /// <remarks>
    ///     One failed probe a minute in a scene that genuinely needs its power, against finding a
    ///     lighter scene within a minute of it arriving. Chosen with the maintainer, 2026-09-26.
    /// </remarks>
    private const double MaximumDwellMs = 60_000;

    /// <summary>Fresh windows each step of a raise chain is judged over.</summary>
    private const int RaiseJudgeWindows = 3;

    /// <summary>How much the ratio must fall for a step to count as answered.</summary>
    private const double RaiseImprovement = 0.04;

    /// <summary>Unanswered steps that end a raise chain.</summary>
    private const int RaiseUnimprovedSteps = 3;

    /// <summary>Window time an unresponsive hold lasts before evidence starts again.</summary>
    private const double UnresponsiveHoldMs = 20_000;

    /// <summary>Ordinary windows that end a quarantine.</summary>
    private const int QuarantineRecoveryWindows = 3;

    /// <summary>Severe windows in a row before a stall is reconsidered as real load.</summary>
    private const int PersistentStallWindows = 4;

    /// <summary>Judged probe windows the failure test looks back over.</summary>
    private const int ProbeFailureWindows = 3;

    /// <summary>Missed windows within that look-back that fail a probe.</summary>
    private const int ProbeFailureMisses = 2;

    /// <summary>Below this GPU load a stall looks like loading rather than demand.</summary>
    private const double StallLoadingGpu = 40;

    /// <summary>At or above this GPU load a stall may be real work after all.</summary>
    private const double StallPowerBoundGpu = 60;

    /// <summary>Below this GPU load a sustained miss is worth waiting out before adding power.</summary>
    private const double DeferralGpu = 50;

    /// <summary>Further missed windows a deferred raise waits through.</summary>
    private const int DeferralWindows = 6;

    /// <summary>At or above this GPU load a step counts as answered however the ratio moved.</summary>
    private const double ResponsiveGpu = 85;

    /// <summary>How far GPU load must climb across a probe for its failure to be power-correlated.</summary>
    private const double ProbeGpuRise = 15;

    /// <summary>How long an absence may last before the streaks are dropped.</summary>
    private const double TelemetryGapResetMs = 3000;

    private double _backoffMs = StabilityDwellMs;
    private string _contextKey = string.Empty;
    private int _deferredWindows;
    private bool _deferring;
    private double _dwellMisses;
    private double _dwellMs;
    private double _elapsedMs;
    private bool _hasElapsed;
    private bool _hasWindow;
    private double _lastGapMs;
    private bool _lastHiatus;
    private double _lastPresentElapsedMs;
    private double _lastRatio = double.NaN;
    private string? _lastUtilization;
    private uint _lastWindowEnd;
    private uint _lastWindowStart;
    private double? _missGpu;
    private double _missRatio0;
    private double _missRatio1;
    private double _missRatio2;
    private int _misses;
    private bool _previousMissed;
    private double? _probeGpuBefore;
    private int _probeJudged;
    private double? _probeMissGpu;
    private int _probeMissHistory;
    private double _raiseBaseline = double.NaN;
    private int _raiseJudged;
    private double? _raiseJudgedGpu;
    private double _raiseRatio0;
    private double _raiseRatio1;
    private double _raiseRatio2;
    private int _raiseUnimproved;
    private int _recoveredWindows;
    private double _settlingMs;
    private AutoTdpPhase _settlingReturn = AutoTdpPhase.Tracking;
    private int _settlingWindows;
    private int _severeWindows;
    private double _unresponsiveMs;
    private AutoTdpWindowClass _windowClass = AutoTdpWindowClass.None;

    /// <summary>The limit the controller believes is in effect.</summary>
    internal int Watts { get; private set; }

    /// <summary>Whether a manual change has suspended automatic control.</summary>
    internal bool IsPaused { get; private set; }

    /// <summary>Where the control loop currently is.</summary>
    internal AutoTdpPhase Phase { get; private set; } = AutoTdpPhase.Tracking;

    /// <summary>Whether a downward probe is in effect, including the settling that follows its write.</summary>
    internal bool IsProbing => Phase is AutoTdpPhase.Probing
                               || (Phase is AutoTdpPhase.Settling && _settlingReturn is AutoTdpPhase.Probing);

    /// <summary>The limit AutoTDP found before the current probe.</summary>
    internal int LastGood { get; private set; }

    /// <summary>Starts control from the limit currently in effect.</summary>
    /// <param name="watts">The limit AutoTDP is taking over from.</param>
    /// <param name="limits">The device bounds.</param>
    /// <param name="contextKey">Application plus target context to start in.</param>
    /// <returns>The limit to begin at.</returns>
    /// <remarks>
    ///     Always the hardware's own value. A stored starting point used to replace it, so a controller
    ///     resuming on a device sitting at 37 W believed 28 W, judged three windows against a limit that
    ///     was never in effect, and then cut 8 W in a single write it called a one-step raise.
    /// </remarks>
    internal int Start(int watts, AutoTdpLimits limits, string contextKey)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _contextKey = contextKey;
        Watts = limits.Clamp(watts);
        LastGood = Watts;
        IsPaused = false;
        ResetEvidence();
        ResetBackoff();
        Phase = AutoTdpPhase.Tracking;
        _hasWindow = false;
        _hasElapsed = false;
        return Watts;
    }

    /// <summary>Suspends automatic control because the limit was changed by hand.</summary>
    /// <param name="watts">The limit the user or a profile just set.</param>
    /// <remarks>
    ///     The pause lasts until AutoTDP is switched off and on again. A user who moved the slider is
    ///     telling the controller its answer was wrong, and silently taking the limit back a few seconds
    ///     later is the single most confusing thing this feature could do.
    /// </remarks>
    internal void PauseForManualChange(int watts)
    {
        IsPaused = true;
        Watts = watts;
        LastGood = watts;
        ResetEvidence();
        ResetBackoff();
        Phase = AutoTdpPhase.Tracking;
    }

    /// <summary>Lifts a manual pause so automatic control judges the next window again.</summary>
    /// <remarks>
    ///     The counterpart to <see cref="PauseForManualChange" /> for a <em>scoped</em> override: a limit
    ///     set for one application pauses control while that application runs, and leaving the application
    ///     must return control rather than leave it paused forever. This does not itself pick a wattage —
    ///     the caller re-bases the controller on the value actually on the device next window, exactly as
    ///     it does after an unapplied write — so the pause simply ends.
    ///     <para>
    ///         It is deliberately distinct from a user's global manual change, which still pauses until
    ///         AutoTDP is switched off and on: that is the user overriding the controller, not a per-game
    ///         profile expiring.
    ///     </para>
    /// </remarks>
    internal void ResumeAutomaticControl()
    {
        IsPaused = false;
        ResetEvidence();
        ResetBackoff();
        Phase = AutoTdpPhase.Tracking;
    }

    /// <summary>Judges one control tick.</summary>
    /// <param name="observation">What the service read for this tick.</param>
    /// <param name="limits">Current device bounds.</param>
    /// <returns>What should happen to the power limit.</returns>
    internal AutoTdpDecision Observe(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(limits);
        _windowClass = AutoTdpWindowClass.None;
        _lastRatio = double.NaN;
        _lastUtilization = null;
        AdvanceClock(observation);
        if (IsPaused)
        {
            return Hold("paused-manual");
        }

        if (!limits.IsUsable)
        {
            return Hold("limits-unusable");
        }

        if (observation.ContextKey is { Length: > 0 } context
            && !string.Equals(context, _contextKey, StringComparison.Ordinal))
        {
            // A different game, or the same game at a different deadline. Carrying evidence across
            // the boundary would judge the new problem on the old one's windows.
            _contextKey = context;
            ResetEvidence();
            ResetBackoff();
            Phase = AutoTdpPhase.Tracking;
            LastGood = Watts;
            _hasWindow = false;
            return Hold("context-changed");
        }

        var fresh = TakeFreshWindow(observation);
        var gap = _elapsedMs - _lastPresentElapsedMs;
        _lastGapMs = gap;
        _lastHiatus = observation.HasTarget && gap >= NominalWindowMs + HiatusMs(observation);
        if (_lastHiatus)
        {
            return EnterQuarantine(true, observation, limits);
        }

        if (fresh is null)
        {
            // A repeated read, or nothing rendering. Neither is evidence; a long enough absence
            // drops the streaks so a gap cannot be stitched onto the windows either side of it.
            if (gap >= TelemetryGapResetMs)
            {
                _misses = 0;
                _dwellMs = 0;
                _dwellMisses = 0;
                _previousMissed = false;
            }

            return Hold(observation.Window is null ? "no-telemetry" : "window-repeat");
        }

        if (!observation.HasTarget || !fresh.IsMeasured)
        {
            ResetEvidence();
            return Hold("no-telemetry");
        }

        _windowClass = Classify(fresh, observation);
        return Phase switch
        {
            AutoTdpPhase.Settling => Settle(fresh, observation, limits),
            AutoTdpPhase.Quarantine => JudgeQuarantine(observation, limits),
            AutoTdpPhase.Probing => JudgeProbe(observation, limits),
            AutoTdpPhase.Raising => JudgeRaise(observation, limits),
            AutoTdpPhase.Unresponsive => JudgeUnresponsive(fresh),
            _ => Track(fresh, observation, limits)
        };
    }

    /// <summary>Ends automatic control and reports the limit to restore.</summary>
    /// <param name="restoreTo">The manual or profile limit AutoTDP took over from.</param>
    /// <returns>The release decision.</returns>
    internal AutoTdpDecision Stop(int restoreTo)
    {
        IsPaused = false;
        Phase = AutoTdpPhase.Tracking;
        ResetEvidence();
        ResetBackoff();
        Watts = restoreTo;
        LastGood = restoreTo;
        return new AutoTdpDecision(AutoTdpAction.Release, restoreTo, "stopped");
    }

    /// <summary>Captures the controller's evidence without changing it.</summary>
    /// <returns>The current snapshot.</returns>
    internal AutoTdpControllerSnapshot Snapshot()
    {
        return new AutoTdpControllerSnapshot(
            _contextKey,
            Watts,
            LastGood,
            Phase,
            IsPaused,
            _windowClass,
            _lastRatio,
            _lastGapMs,
            _lastHiatus,
            _misses,
            _dwellMs,
            _backoffMs,
            _raiseBaseline,
            _raiseUnimproved,
            _probeJudged,
            _settlingWindows,
            _severeWindows,
            _lastUtilization);
    }

    /// <summary>The gap that separates a hitch from a hiatus at this target.</summary>
    private static double HiatusMs(AutoTdpObservation observation)
    {
        return Math.Max(MinimumHiatusMs, HiatusFrames * observation.TargetFrametimeMs);
    }

    private static double Median(double first, double second, double third)
    {
        return Math.Max(Math.Min(first, second), Math.Min(Math.Max(first, second), third));
    }

    private void AdvanceClock(AutoTdpObservation observation)
    {
        if (!_hasElapsed)
        {
            _hasElapsed = true;
            _lastPresentElapsedMs = observation.ElapsedMs;
        }

        _elapsedMs = observation.ElapsedMs;
        if (observation.Window is { } window)
        {
            // When the last frame was presented, on the caller's clock. Taken from every read, not
            // only a fresh one, because a window whose age keeps growing is the stall itself.
            _lastPresentElapsedMs = Math.Max(_lastPresentElapsedMs, observation.ElapsedMs - window.AgeMs);
        }
    }

    /// <summary>Returns the window only when it is one this controller has not judged.</summary>
    private AutoTdpWindow? TakeFreshWindow(AutoTdpObservation observation)
    {
        if (observation.Window is not { } window)
        {
            return null;
        }

        if (_hasWindow && window.StartTicks == _lastWindowStart && window.EndTicks == _lastWindowEnd)
        {
            return null;
        }

        _hasWindow = true;
        _lastWindowStart = window.StartTicks;
        _lastWindowEnd = window.EndTicks;
        return window;
    }

    private AutoTdpWindowClass Classify(AutoTdpWindow window, AutoTdpObservation observation)
    {
        var ratio = window.MeanFrametimeMs / observation.TargetFrametimeMs;
        _lastRatio = ratio;
        var hiatus = HiatusMs(observation);
        if (window.LastFrameMs >= hiatus || ratio >= SevereRatio || window.DurationMs >= SevereDurationMs)
        {
            // Three detectors for one condition, and the captures need all three: a blocked render
            // thread that is still blocked shows up as the gap, one that unblocked at the end of the
            // window shows up as the last frame, and a loading interval of many slow frames shows up
            // only in the ratio.
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

    private AutoTdpDecision Settle(AutoTdpWindow window, AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        _settlingWindows++;
        _settlingMs += window.DurationMs;
        if (_settlingWindows < SettleWindows || _settlingMs < SettleMs)
        {
            return Hold("settling");
        }

        Phase = _settlingReturn;
        _settlingWindows = 0;
        _settlingMs = 0;
        return Hold("settling");
    }

    private AutoTdpDecision Track(AutoTdpWindow window, AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        if (_windowClass is AutoTdpWindowClass.Missed)
        {
            return CountMiss(observation, limits);
        }

        _misses = 0;
        _missGpu = null;
        _previousMissed = false;
        if (_deferring)
        {
            // Delivery recovered on its own. Whatever the misses were, they were not a power request.
            _deferring = false;
            _deferredWindows = 0;
        }

        if (_windowClass is AutoTdpWindowClass.OnTarget)
        {
            // Meeting the deadline without headroom is the state AutoTDP is aiming for. Probing down
            // from here would cost the frames it just secured.
            _dwellMs = 0;
            _dwellMisses = 0;
            return Hold("on-target");
        }

        _dwellMs += window.DurationMs;
        return _dwellMs < _backoffMs ? Hold("tracking-headroom") : BeginProbe(observation, limits);
    }

    private AutoTdpDecision CountMiss(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        // The dwell tolerates a single isolated hitch, but not two of them and not two in a row.
        if (_previousMissed || _dwellMisses >= 1)
        {
            _dwellMs = 0;
            _dwellMisses = 0;
        }
        else
        {
            _dwellMisses++;
        }

        _previousMissed = true;
        _missRatio2 = _missRatio1;
        _missRatio1 = _missRatio0;
        _missRatio0 = _lastRatio;
        if (observation.GpuLoadPercent is { } load && (_missGpu is null || load > _missGpu))
        {
            _missGpu = load;
        }

        _misses = Math.Min(_misses + 1, SustainedMisses);
        if (_misses < SustainedMisses)
        {
            return Hold("miss-unconfirmed");
        }

        if (limits.Clamp(Watts + limits.Step) <= Watts)
        {
            return Hold("at-maximum");
        }

        if (_deferring)
        {
            _deferredWindows++;
            if (_deferredWindows < DeferralWindows)
            {
                return Hold("miss-deferred");
            }

            // Waited it out and the misses are still here. Utilization delays a raise; it never
            // vetoes one.
            _deferring = false;
            _deferredWindows = 0;
            _lastUtilization = "deferral-expired";
            return Raise(limits, "sustained-miss");
        }

        if (_missGpu < DeferralGpu)
        {
            // Every one of these windows was late while the GPU idled. That is what a loading
            // screen or a blocked render thread looks like, and adding power to one buys nothing.
            _deferring = true;
            _deferredWindows = 0;
            _lastUtilization = "raise-deferred-low-gpu";
            return Hold("miss-deferred");
        }

        return Raise(limits, "sustained-miss");
    }

    private AutoTdpDecision JudgeRaise(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        _raiseRatio2 = _raiseRatio1;
        _raiseRatio1 = _raiseRatio0;
        _raiseRatio0 = _lastRatio;
        _raiseJudged++;
        if (observation.GpuLoadPercent is { } gpu && (_raiseJudgedGpu is null || gpu > _raiseJudgedGpu))
        {
            _raiseJudgedGpu = gpu;
        }

        if (_raiseJudged < RaiseJudgeWindows)
        {
            return Hold("raise-pending");
        }

        var median = Median(_raiseRatio0, _raiseRatio1, _raiseRatio2);
        var responsiveGpu = _raiseJudgedGpu >= ResponsiveGpu;
        var resolved = median <= MissRatio;
        _raiseJudged = 0;
        _raiseJudgedGpu = null;
        if (resolved)
        {
            Phase = AutoTdpPhase.Tracking;
            ResetEvidence();
            return Hold("raise-resolved");
        }

        if (median <= _raiseBaseline - RaiseImprovement || responsiveGpu)
        {
            if (responsiveGpu)
            {
                _lastUtilization = "raise-responsive-gpu";
            }

            _raiseUnimproved = 0;
            _raiseBaseline = median;
            return Raise(limits, "sustained-miss");
        }

        _raiseUnimproved++;
        if (_raiseUnimproved < RaiseUnimprovedSteps)
        {
            _raiseBaseline = median;
            return Raise(limits, "sustained-miss");
        }

        // Three steps and delivery never answered. Whatever is late is not waiting on watts.
        Phase = AutoTdpPhase.Unresponsive;
        _unresponsiveMs = 0;
        return Hold("unresponsive");
    }

    private AutoTdpDecision JudgeUnresponsive(AutoTdpWindow window)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            Phase = AutoTdpPhase.Quarantine;
            _severeWindows = 1;
            _recoveredWindows = 0;
            return Hold("quarantine-stall");
        }

        _unresponsiveMs += window.DurationMs;
        if (_windowClass is not AutoTdpWindowClass.Missed || _unresponsiveMs >= UnresponsiveHoldMs)
        {
            // Either delivery came back, in which case the probe path recovers the steps, or the
            // hold expired and a later sustained miss deserves to be judged from scratch.
            Phase = AutoTdpPhase.Tracking;
            ResetEvidence();
            return Hold("unresponsive-ended");
        }

        return Hold("unresponsive");
    }

    private AutoTdpDecision EnterQuarantine(bool hiatus, AutoTdpObservation observation, AutoTdpLimits limits)
    {
        var probing = IsProbing;
        var reason = hiatus ? "quarantine-hiatus" : "quarantine-stall";
        Phase = AutoTdpPhase.Quarantine;
        _recoveredWindows = 0;
        _severeWindows++;
        _misses = 0;
        _missGpu = null;
        _previousMissed = false;
        _deferring = false;
        _deferredWindows = 0;
        _dwellMs = 0;
        _dwellMisses = 0;
        _raiseJudged = 0;
        _raiseJudgedGpu = null;
        _raiseUnimproved = 0;
        _raiseBaseline = double.NaN;
        _settlingWindows = 0;
        _settlingMs = 0;
        if (!probing)
        {
            return CheckPersistentStall(observation, limits) ?? Hold(reason);
        }

        // A probe cannot be judged by a stall. Go back to the limit that was delivering and let the
        // result expire unlearned, so the same step is offered again once frames return.
        _probeJudged = 0;
        _probeMissHistory = 0;
        _probeGpuBefore = null;
        _probeMissGpu = null;
        Watts = LastGood;
        return new AutoTdpDecision(AutoTdpAction.Restore, Watts, "probe-interrupted");
    }

    private AutoTdpDecision JudgeQuarantine(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            _severeWindows++;
            _recoveredWindows = 0;
            return CheckPersistentStall(observation, limits) ?? Hold("quarantine-stall");
        }

        _severeWindows = 0;
        _recoveredWindows++;
        if (_recoveredWindows < QuarantineRecoveryWindows)
        {
            return Hold("quarantine-recovery");
        }

        // Normal presentation is back. Everything the stall touched is discarded, including the
        // backoff: a loading screen usually means a new area whose power needs are unknown.
        Phase = AutoTdpPhase.Tracking;
        ResetEvidence();
        ResetBackoff();
        return Hold("quarantine-ended");
    }

    /// <summary>Reconsiders a stall that has lasted long enough to be real work rather than loading.</summary>
    /// <returns>A raise when the evidence allows one, else null to stay quarantined.</returns>
    private AutoTdpDecision? CheckPersistentStall(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_severeWindows < PersistentStallWindows)
        {
            return null;
        }

        if (observation.GpuLoadPercent is { } gpu && gpu < StallPowerBoundGpu)
        {
            // Long, and the GPU is not working for it. A loading screen is not a power request
            // however long it takes. Hold the count so the test is re-applied, not re-armed.
            _lastUtilization = gpu < StallLoadingGpu ? "stall-loading" : "stall-inconclusive";
            _severeWindows = PersistentStallWindows;
            return null;
        }

        // Either the GPU is genuinely busy through this, or there is no sensor to say otherwise.
        // Treat it as load; the raise chain's response test takes the steps back if it was not.
        _lastUtilization = observation.GpuLoadPercent is null ? "stall-no-gpu" : "stall-power-bound";
        _severeWindows = 0;
        _recoveredWindows = 0;
        _raiseBaseline = double.IsNaN(_lastRatio) ? SevereRatio : _lastRatio;
        _raiseUnimproved = 0;
        return Raise(limits, "stall-power-bound");
    }

    private AutoTdpDecision BeginProbe(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        var candidate = limits.Clamp(Watts - limits.Step);
        if (candidate >= Watts)
        {
            return Hold("at-minimum");
        }

        LastGood = Watts;
        Watts = candidate;
        _probeJudged = 0;
        _probeMissHistory = 0;
        _probeMissGpu = null;
        _probeGpuBefore = observation.GpuLoadPercent;
        _dwellMs = 0;
        _dwellMisses = 0;
        _misses = 0;
        EnterSettling(AutoTdpPhase.Probing);
        return new AutoTdpDecision(AutoTdpAction.Probe, Watts, "probe-down");
    }

    private AutoTdpDecision JudgeProbe(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        var missed = _windowClass is AutoTdpWindowClass.Missed;
        _probeMissHistory = ((_probeMissHistory << 1) | (missed ? 1 : 0)) & ((1 << ProbeFailureWindows) - 1);
        _probeJudged++;
        if (missed && observation.GpuLoadPercent is { } gpu && (_probeMissGpu is null || gpu > _probeMissGpu))
        {
            _probeMissGpu = gpu;
        }

        if (BitOperations.PopCount((uint)_probeMissHistory) >= ProbeFailureMisses)
        {
            return FailProbe();
        }

        if (_probeJudged < ProbeWindows)
        {
            return Hold("probe-pending");
        }

        // The lower limit delivered. It is simply the operating point now, and the next dwell may
        // take another step off it.
        Phase = AutoTdpPhase.Tracking;
        LastGood = Watts;
        ResetEvidence();
        ResetBackoff();
        return Hold("probe-accepted");
    }

    private AutoTdpDecision FailProbe()
    {
        var confirmed = IsProbeFailureConfirmed();
        Watts = LastGood;
        _probeJudged = 0;
        _probeMissHistory = 0;
        _probeMissGpu = null;
        _probeGpuBefore = null;
        _dwellMs = 0;
        _dwellMisses = 0;
        _misses = 0;
        EnterSettling(AutoTdpPhase.Tracking);
        if (!confirmed)
        {
            return new AutoTdpDecision(AutoTdpAction.Restore, Watts, "probe-inconclusive");
        }

        // The step was load-bearing and the evidence says so. Wait longer before offering it again,
        // but never stop offering it: the scene this was measured in will not last forever.
        _backoffMs = Math.Min(_backoffMs * 2, MaximumDwellMs);
        return new AutoTdpDecision(AutoTdpAction.Restore, Watts, "probe-failed");
    }

    /// <summary>Whether a failed probe's misses actually correlate with the power that was removed.</summary>
    private bool IsProbeFailureConfirmed()
    {
        if (_probeMissGpu is not { } gpu)
        {
            // No sensor. Frame delivery is the only evidence there is, and it says the step hurt.
            return true;
        }

        if (gpu >= DeferralGpu)
        {
            _lastUtilization = "probe-failed-loaded";
            return true;
        }

        if (_probeGpuBefore is { } before && gpu - before >= ProbeGpuRise)
        {
            _lastUtilization = "probe-failed-gpu-rose";
            return true;
        }

        // Frames went late while the GPU stayed idle, and it did not climb when the power came off.
        // Nothing here says the watts were the problem, so nothing is held against this step.
        _lastUtilization = "probe-inconclusive-low-gpu";
        return false;
    }

    private AutoTdpDecision Raise(AutoTdpLimits limits, string reason)
    {
        var candidate = limits.Clamp(Watts + limits.Step);
        if (candidate <= Watts)
        {
            return Hold("at-maximum");
        }

        if (double.IsNaN(_raiseBaseline))
        {
            _raiseBaseline = double.IsNaN(_lastRatio)
                ? MissRatio
                : Median(_missRatio0, _missRatio1, _missRatio2);
        }

        Watts = candidate;
        LastGood = candidate;
        _misses = 0;
        _missGpu = null;
        _dwellMs = 0;
        _dwellMisses = 0;
        _previousMissed = false;
        ResetBackoff();
        EnterSettling(AutoTdpPhase.Raising);
        return new AutoTdpDecision(AutoTdpAction.Raise, Watts, reason);
    }

    private void EnterSettling(AutoTdpPhase next)
    {
        Phase = AutoTdpPhase.Settling;
        _settlingReturn = next;
        _settlingWindows = 0;
        _settlingMs = 0;
    }

    private AutoTdpDecision Hold(string reason)
    {
        return new AutoTdpDecision(AutoTdpAction.Hold, Watts, reason);
    }

    /// <summary>Drops every streak, counter and in-flight judgement. Never touches the backoff.</summary>
    private void ResetEvidence()
    {
        _misses = 0;
        _missGpu = null;
        _previousMissed = false;
        _dwellMs = 0;
        _dwellMisses = 0;
        _deferring = false;
        _deferredWindows = 0;
        _raiseJudged = 0;
        _raiseJudgedGpu = null;
        _raiseUnimproved = 0;
        _raiseBaseline = double.NaN;
        _probeJudged = 0;
        _probeMissHistory = 0;
        _probeGpuBefore = null;
        _probeMissGpu = null;
        _settlingWindows = 0;
        _settlingMs = 0;
        _severeWindows = 0;
        _recoveredWindows = 0;
        _unresponsiveMs = 0;
    }

    private void ResetBackoff()
    {
        _backoffMs = StabilityDwellMs;
    }
}

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
