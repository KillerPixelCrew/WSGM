using System;

namespace WSGM.Core;

/// <summary>
///     The one deterministic AutoTDP control policy.
/// </summary>
/// <remarks>
///     Delivered frames decide; utilization adjusts confidence in what a frame-time event meant and
///     can slow a descent, but never sizes a step from its own level. The places it is consulted are
///     named in <c>docs\autotdp-controller.md</c>, and each is skipped when no sensor provider is
///     publishing, so the controller degrades to frametime-only rather than behaving differently.
///     <para>
///         No learned floor survives a probe, context, or session. Fresh evidence is required, and
///         bounded backoff slows repeated probes at one operating point without forbidding them.
///     </para>
///     <para>
///         Pure and single-threaded on purpose. Every input arrives as an argument, every decision is a
///         return value, and the whole controller replays exactly from a recorded trace — which is how a
///         reported oscillation gets reproduced without the hardware that produced it. This type owns
///         the phase machine; <see cref="AutoTdpWindowClassifier" /> classifies windows,
///         <see cref="AutoTdpStepSizing" /> sizes steps, <see cref="AutoTdpPresentClock" /> measures the
///         present gap, and <see cref="AutoTdpRaiseChain" /> and <see cref="AutoTdpProbeTrial" /> hold
///         the evidence a raise and a probe are judged on.
///     </para>
/// </remarks>
internal sealed class AutoTdpController
{
    /// <summary>Consecutive missed windows before power is raised.</summary>
    /// <remarks>
    ///     Two, not three: a descent now takes back an unneeded raise within seconds, so a raise that
    ///     turns out to be early costs far less than a stutter that runs a window longer.
    /// </remarks>
    internal const int SustainedMisses = 2;

    /// <summary>Windows ignored after a write, while the limit takes effect.</summary>
    internal const int SettleWindows = 2;

    /// <summary>Below this GPU load a sustained miss is worth waiting out before adding power.</summary>
    internal const double DeferralGpu = 50;

    /// <summary>Window time a write is given before anything is judged again.</summary>
    private const double SettleMs = 2000;

    /// <summary>Stable window time before the first downward probe at an operating point.</summary>
    /// <remarks>
    ///     The dwell establishes a steady scene; probe windows judge the step. Failure doubles this
    ///     delay up to the maximum dwell without creating a permanent wattage floor.
    /// </remarks>
    private const double StabilityDwellMs = 5_000;

    /// <summary>The longest the backoff may stretch that dwell.</summary>
    /// <remarks>
    ///     One failed probe a minute in a scene that genuinely needs its power, against finding a
    ///     lighter scene within a minute of it arriving. Chosen with the maintainer, 2026-09-26.
    /// </remarks>
    private const double MaximumDwellMs = 60_000;

    /// <summary>Window time an unresponsive hold lasts before evidence starts again.</summary>
    private const double UnresponsiveHoldMs = 20_000;

    /// <summary>Ordinary windows that end a quarantine.</summary>
    private const int QuarantineRecoveryWindows = 3;

    /// <summary>Severe windows in a row before a stall is reconsidered as real load.</summary>
    private const int PersistentStallWindows = 4;

    /// <summary>Below this GPU load a stall looks like loading rather than demand.</summary>
    private const double StallLoadingGpu = 40;

    /// <summary>Further missed windows a deferred raise waits through.</summary>
    private const int DeferralWindows = 6;

    /// <summary>How long an absence may last before the streaks are dropped.</summary>
    private const double TelemetryGapResetMs = 3000;

    private readonly AutoTdpPresentClock _clock = new();
    private readonly AutoTdpProbeTrial _probe = new();
    private readonly AutoTdpRaiseChain _raise = new();

    private double _backoffMs = StabilityDwellMs;
    private string _contextKey = string.Empty;
    private int _deferredWindows;
    private bool _deferring;
    private bool _descending;
    private int? _descentFailedWatts;
    private double _dwellMisses;
    private double _dwellMs;
    private double _lastGapMs;
    private bool _lastHiatus;
    private double _lastRatio = double.NaN;
    private string? _lastUtilization;
    private AutoTdpLoadAverage _loadCpu;
    private AutoTdpLoadAverage _loadGpu;
    private double? _missGpu;
    private double _missRatio0;
    private double _missRatio1;
    private int _misses;
    private bool _previousMissed;
    private int _probeSteps = 1;
    private int _recoveredWindows;
    private double _settlingMs;
    private AutoTdpPhase _settlingReturn = AutoTdpPhase.Tracking;
    private int _settlingWindows;
    private int _severeWindows;
    private AutoTdpLoadAverage _stallGpu;
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

    /// <summary>The stable window time the next downward probe needs.</summary>
    private double RequiredDwellMs => _descending ? 0 : _backoffMs;

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
        _clock.Restart();
        return Watts;
    }

    /// <summary>Suspends automatic control because the limit was changed by hand.</summary>
    /// <param name="watts">The limit the user or a profile just set, or null when a firmware profile owns it.</param>
    /// <remarks>
    ///     The pause lasts until AutoTDP is switched off and on again. A user who moved the slider is
    ///     telling the controller its answer was wrong, and silently taking the limit back a few seconds
    ///     later is the single most confusing thing this feature could do.
    /// </remarks>
    internal void PauseForManualChange(int? watts)
    {
        IsPaused = true;
        if (watts is { } known)
        {
            Watts = known;
            LastGood = known;
        }

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
        _clock.Advance(observation);
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
            _clock.ForgetWindow();
            return Hold("context-changed");
        }

        var fresh = _clock.TakeFresh(observation);
        var gap = _clock.GapMs;
        _lastGapMs = gap;
        _lastHiatus = AutoTdpWindowClassifier.IsHiatus(gap, observation);
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
                ResetLoad();
            }

            return Hold(observation.Window is null ? "no-telemetry" : "window-repeat");
        }

        if (!observation.HasTarget || !fresh.IsMeasured)
        {
            ResetEvidence();
            return Hold("no-telemetry");
        }

        _windowClass = AutoTdpWindowClassifier.Classify(fresh, observation, out _lastRatio, out var heavyScene);
        if (heavyScene)
        {
            _lastUtilization = "heavy-scene";
        }

        return Phase switch
        {
            AutoTdpPhase.Settling => Settle(fresh, observation, limits),
            AutoTdpPhase.Quarantine => JudgeQuarantine(observation, limits),
            AutoTdpPhase.Probing => JudgeProbe(observation, limits),
            AutoTdpPhase.Raising => JudgeRaise(observation, limits),
            AutoTdpPhase.Unresponsive => JudgeUnresponsive(fresh, observation),
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
            RequiredDwellMs,
            _raise.Baseline,
            _raise.Unimproved,
            _probe.Judged,
            _settlingWindows,
            _severeWindows,
            _lastUtilization);
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
            EndDescent();
            ResetLoad();
            return Hold("on-target");
        }

        _dwellMs += window.DurationMs;
        SampleLoad(observation);
        return _dwellMs < RequiredDwellMs ? Hold("tracking-headroom") : BeginProbe(observation, limits);
    }

    private AutoTdpDecision CountMiss(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        // A late window straight after an accepted probe says the new limit is close to the edge.
        // Further steps down wait for an ordinary dwell, and load read around a miss is not headroom.
        EndDescent();
        ResetLoad();
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
            return Raise(limits, "sustained-miss", MissLevel());
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

        return Raise(limits, "sustained-miss", MissLevel());
    }

    private AutoTdpDecision JudgeRaise(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        if (!_raise.TryJudge(_lastRatio, observation.GpuLoadPercent, out var level, out var responsiveGpu))
        {
            return Hold("raise-pending");
        }

        if (level <= AutoTdpWindowClassifier.MissRatio)
        {
            Phase = AutoTdpPhase.Tracking;
            ResetEvidence();
            return Hold("raise-resolved");
        }

        if (_raise.IsAnswered(level, responsiveGpu))
        {
            if (responsiveGpu)
            {
                _lastUtilization = "raise-responsive-gpu";
            }

            _raise.Answered(level);
            return Raise(limits, "sustained-miss", level);
        }

        if (_raise.Unanswered(level))
        {
            // Delivery did not answer the last raise, so the next one is a single step: power that
            // is not being turned into frames is not sized up.
            return Raise(limits, "sustained-miss", double.NaN);
        }

        // Three steps and delivery never answered. Whatever is late is not waiting on watts.
        Phase = AutoTdpPhase.Unresponsive;
        _unresponsiveMs = 0;
        return Hold("unresponsive");
    }

    private AutoTdpDecision JudgeUnresponsive(AutoTdpWindow window, AutoTdpObservation observation)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            Phase = AutoTdpPhase.Quarantine;
            _severeWindows = 1;
            _recoveredWindows = 0;
            _stallGpu = default;
            _stallGpu.Add(observation.GpuLoadPercent);
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
        EndDescent();
        ResetLoad();
        Phase = AutoTdpPhase.Quarantine;
        _recoveredWindows = 0;
        _severeWindows++;
        _stallGpu.Add(observation.GpuLoadPercent);
        _misses = 0;
        _missGpu = null;
        _previousMissed = false;
        _deferring = false;
        _deferredWindows = 0;
        _dwellMs = 0;
        _dwellMisses = 0;
        _raise.Reset();
        _settlingWindows = 0;
        _settlingMs = 0;
        if (!probing)
        {
            return CheckPersistentStall(observation, limits) ?? Hold(reason);
        }

        // A probe cannot be judged by a stall. Go back to the limit that was delivering and let the
        // result expire unlearned, so the same step is offered again once frames return.
        _probe.Reset();
        Watts = LastGood;
        return new AutoTdpDecision(AutoTdpAction.Restore, Watts, "probe-interrupted");
    }

    private AutoTdpDecision JudgeQuarantine(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            _severeWindows++;
            _recoveredWindows = 0;
            _stallGpu.Add(observation.GpuLoadPercent);
            return CheckPersistentStall(observation, limits) ?? Hold("quarantine-stall");
        }

        _severeWindows = 0;
        _stallGpu = default;
        if (_windowClass is AutoTdpWindowClass.Missed
            && observation.GpuLoadPercent >= AutoTdpWindowClassifier.StallPowerBoundGpu)
        {
            // Frames still late and the GPU working for them: this is the scene, not its loading.
            // The recovery windows exist to discard post-loading frames, and these are not that;
            // waiting them out only delayed the raise they were asking for (Claw, 2026-09-27).
            _lastUtilization = "recovery-power-bound";
            Phase = AutoTdpPhase.Tracking;
            ResetEvidence();
            ResetBackoff();
            return CountMiss(observation, limits);
        }

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

        // The GPU over the whole stall, not the tick that happened to end it: a loading screen's
        // last window is read as the game starts drawing again, and one 70 % reading after ten
        // seconds at 5 % doubled the limit for a scene that then ran capped (Claw, 2026-09-27).
        if (_stallGpu.Mean is { } gpu && gpu < AutoTdpWindowClassifier.StallPowerBoundGpu)
        {
            // Long, and the GPU is not working for it. A loading screen is not a power request
            // however long it takes. Hold the count so the test is re-applied, not re-armed.
            _lastUtilization = gpu < StallLoadingGpu ? "stall-loading" : "stall-inconclusive";
            _severeWindows = PersistentStallWindows;
            return null;
        }

        // Either the GPU is genuinely busy through this, or there is no sensor to say otherwise.
        // Treat it as load; the raise chain's response test takes the steps back if it was not.
        _lastUtilization = _stallGpu.Mean is null ? "stall-no-gpu" : "stall-power-bound";
        _stallGpu = default;
        _severeWindows = 0;
        _recoveredWindows = 0;
        _raise.Restart(double.IsNaN(_lastRatio) ? AutoTdpWindowClassifier.SevereRatio : _lastRatio);
        return Raise(limits, "stall-power-bound", _raise.Baseline);
    }

    private AutoTdpDecision BeginProbe(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        var steps = AutoTdpStepSizing.ProbeSteps(Watts, _probeSteps, _descentFailedWatts, limits);
        if (steps == 0)
        {
            // The descent is one step above a limit it already found too low. It ends here, and the
            // next probe waits for an ordinary dwell like any other.
            EndDescent();
            _dwellMs = 0;
            _dwellMisses = 0;
            ResetLoad();
            return Hold("tracking-headroom");
        }

        var candidate = limits.Clamp(Watts - steps * limits.Step);
        if (candidate >= Watts)
        {
            return Hold("at-minimum");
        }

        LastGood = Watts;
        Watts = candidate;
        // The load the probe is compared against: the mean over the windows that earned it, which
        // one window's reading could not stand in for.
        _probe.Begin(
            _loadGpu.Mean ?? observation.GpuLoadPercent,
            _loadCpu.Mean ?? observation.CpuLoadPercent);
        _dwellMs = 0;
        _dwellMisses = 0;
        _misses = 0;
        ResetLoad();
        EnterSettling(AutoTdpPhase.Probing);
        return new AutoTdpDecision(AutoTdpAction.Probe, Watts, "probe-down");
    }

    private AutoTdpDecision JudgeProbe(AutoTdpObservation observation, AutoTdpLimits limits)
    {
        if (_windowClass is AutoTdpWindowClass.Severe)
        {
            return EnterQuarantine(false, observation, limits);
        }

        _probe.Record(_windowClass is AutoTdpWindowClass.Missed, observation.GpuLoadPercent);
        SampleLoad(observation);
        if (_probe.HasFailed)
        {
            return FailProbe(limits);
        }

        if (!_probe.IsComplete)
        {
            return Hold("probe-pending");
        }

        // The lower limit delivered. It is simply the operating point now, and the descent carries
        // on without another dwell: the probe's own windows were that evidence, and they are the
        // baseline the next probe's load is compared against. One step per 18 s took four and a
        // half minutes to shed 16 W from a scene that needed 11 W (Claw, 2026-09-27).
        var loadRose = AutoTdpStepSizing.LoadRose(
            _loadGpu.Mean,
            _probe.GpuBefore,
            _loadCpu.Mean,
            _probe.CpuBefore);
        if (loadRose)
        {
            _lastUtilization = "probe-load-rose";
        }

        var steps = AutoTdpStepSizing.NextProbeSteps(_probeSteps, loadRose);
        var failed = _descentFailedWatts;
        var gpuLoad = _loadGpu;
        var cpuLoad = _loadCpu;
        Phase = AutoTdpPhase.Tracking;
        LastGood = Watts;
        ResetEvidence();
        ResetBackoff();
        _descending = true;
        _probeSteps = steps;
        _descentFailedWatts = failed;
        _loadGpu = gpuLoad;
        _loadCpu = cpuLoad;
        return Hold("probe-accepted");
    }

    private AutoTdpDecision FailProbe(AutoTdpLimits limits)
    {
        var confirmed = _probe.IsFailureConfirmed(out var utilization);
        if (utilization is not null)
        {
            _lastUtilization = utilization;
        }

        var failedWatts = Watts;
        var failedSteps = (LastGood - Watts) / limits.Step;
        Watts = LastGood;
        ResetLoad();
        _probe.Reset();
        _dwellMs = 0;
        _dwellMisses = 0;
        _misses = 0;
        EnterSettling(AutoTdpPhase.Tracking);
        var reason = confirmed ? "probe-failed" : "probe-inconclusive";
        if (failedSteps > 1)
        {
            // Too deep, which says nothing about a smaller step. Try half the distance straight away,
            // so a descent converges on the limit instead of stopping a few watts above it. The
            // failed limit bounds only this descent; it is gone the moment the descent ends.
            _descending = true;
            _descentFailedWatts = failedWatts;
            return new AutoTdpDecision(AutoTdpAction.Restore, Watts, reason);
        }

        EndDescent();
        if (confirmed)
        {
            // The single step was load-bearing and the evidence says so. Wait longer before offering
            // it again, but never stop offering it: the scene this was measured in will not last.
            _backoffMs = Math.Min(_backoffMs * 2, MaximumDwellMs);
        }

        return new AutoTdpDecision(AutoTdpAction.Restore, Watts, reason);
    }

    /// <summary>Raises the limit by an amount sized to how late delivery is.</summary>
    /// <param name="limits">The device bounds.</param>
    /// <param name="reason">The decision's reason token.</param>
    /// <param name="ratio">The ratio this raise answers, or NaN for a single step.</param>
    private AutoTdpDecision Raise(AutoTdpLimits limits, string reason, double ratio)
    {
        var candidate = limits.Clamp(Watts + AutoTdpStepSizing.RaiseSteps(Watts, ratio, limits) * limits.Step);
        if (candidate <= Watts)
        {
            return Hold("at-maximum");
        }

        _raise.StartIfIdle(double.IsNaN(_lastRatio) ? AutoTdpWindowClassifier.MissRatio : MissLevel());
        Watts = candidate;
        LastGood = candidate;
        _misses = 0;
        _missGpu = null;
        _dwellMs = 0;
        _dwellMisses = 0;
        _previousMissed = false;
        EndDescent();
        ResetLoad();
        ResetBackoff();
        EnterSettling(AutoTdpPhase.Raising);
        return new AutoTdpDecision(AutoTdpAction.Raise, Watts, reason);
    }

    /// <summary>How late the missed windows that made the case for a raise were.</summary>
    private double MissLevel()
    {
        return (_missRatio0 + _missRatio1) / 2;
    }

    /// <summary>Stops a descent, so the next probe waits for an ordinary dwell.</summary>
    private void EndDescent()
    {
        _descending = false;
        _descentFailedWatts = null;
        _probeSteps = 1;
    }

    /// <summary>Adds one settled window's load to the evidence a probe is compared against.</summary>
    /// <remarks>
    ///     GPU and CPU are kept apart, because the limit feeds both and either may be the one that
    ///     starts to climb as power comes off.
    /// </remarks>
    private void SampleLoad(AutoTdpObservation observation)
    {
        _loadGpu.Add(observation.GpuLoadPercent);
        _loadCpu.Add(observation.CpuLoadPercent);
    }

    private void ResetLoad()
    {
        _loadGpu = default;
        _loadCpu = default;
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
        _raise.Reset();
        _probe.Reset();
        _settlingWindows = 0;
        _settlingMs = 0;
        _severeWindows = 0;
        _stallGpu = default;
        _recoveredWindows = 0;
        _unresponsiveMs = 0;
        EndDescent();
        ResetLoad();
    }

    private void ResetBackoff()
    {
        _backoffMs = StabilityDwellMs;
    }
}
