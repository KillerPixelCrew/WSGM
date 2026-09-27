using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

public sealed class AutoTdpControllerTests
{
    private const string Game = "steam:70|16.60ms";

    private const double Target = 16.6;

    /// <summary>Comfortably inside the deadline.</summary>
    private const double Fast = 12.0;

    /// <summary>Held at the cap by the limiter: the state a converged game sits in.</summary>
    private const double Capped = 16.6;

    /// <summary>Meeting the deadline with nothing to give away.</summary>
    private const double Tight = 16.0;

    /// <summary>Late, but nothing like a stall.</summary>
    private const double Late = 22.0;

    /// <summary>A window so far past its deadline that only a stall explains it.</summary>
    private const double Stalled = 300.0;

    private static readonly AutoTdpLimits Limits = new(8, 30, 2);

    public AutoTdpControllerTests()
    {
        AutoTdpReplay.ResetClock();
    }

    /// <summary>Comfortable windows needed to serve the first dwell at a given operating point.</summary>
    private static int DwellWindows => 10;

    /// <summary>Fresh windows each raise is judged over.</summary>
    private static int RaiseJudgeWindows => 2;

    /// <summary>Severe windows in a row before a stall is reconsidered as load.</summary>
    private static int PersistentStallWindows => 4;

    /// <summary>A sized raise and two unanswered single steps, each settled and judged.</summary>
    private static int UnansweredChainWindows =>
        AutoTdpController.SustainedMisses + (3 * (AutoTdpController.SettleWindows + RaiseJudgeWindows));

    [Fact]
    public void ASingleMissedWindowDoesNotRaisePower()
    {
        var controller = Started(15);

        // Late windows that are not consecutive are hitches, not a sustained miss.
        var decisions = new[] { Run(controller, 1, Late), Run(controller, 1, Capped), Run(controller, 1, Late) }
            .SelectMany(run => run);

        Assert.All(decisions, decision => Assert.Equal(AutoTdpAction.Hold, decision.Action));
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void SustainedMissesRaisePowerByTheDeficit()
    {
        var controller = Started(15);

        // Frames about 33 % late ask for half again as much, about 49 %: 7.3 W, four 2 W steps.
        var decisions = Run(controller, AutoTdpController.SustainedMisses, Late);

        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal("sustained-miss", decisions[^1].Reason);
        Assert.Equal(23, controller.Watts);
    }

    [Fact]
    public void NoRaiseMoreThanDoublesTheLimit()
    {
        var controller = Started(8);

        // A persistent stall's ratio is far past any real deficit; the raise is capped at doubling.
        Run(controller, PersistentStallWindows, Stalled);

        Assert.Equal(16, controller.Watts);
    }

    [Fact]
    public void ARaiseThatDeliveryAnswersKeepsClimbing()
    {
        var controller = Started(8);
        Run(controller, AutoTdpController.SustainedMisses, Late);

        // Each raise buys real frames back, so each one earns the next, sized to what is left.
        Run(controller, AutoTdpController.SettleWindows, 21.0);
        var first = Run(controller, RaiseJudgeWindows, 21.0);
        Run(controller, AutoTdpController.SettleWindows, 19.5);
        var second = Run(controller, RaiseJudgeWindows, 19.5);

        Assert.Equal(AutoTdpAction.Raise, first[^1].Action);
        Assert.Equal(18, first[^1].Watts);
        Assert.Equal(AutoTdpAction.Raise, second[^1].Action);
        Assert.Equal(24, controller.Watts);
    }

    [Fact]
    public void ARaiseChainStopsAfterThreeStepsDeliveryDidNotAnswer()
    {
        var controller = Started(15);

        // Two misses raise by the deficit, then unanswered raises go a single step at a time and the
        // third of them ends the chain.
        var decisions = Run(controller, UnansweredChainWindows, Late);

        Assert.Equal(
            new[] { 23, 25, 27 },
            decisions.Where(decision => decision.Action is AutoTdpAction.Raise).Select(decision => decision.Watts));
        Assert.Equal(AutoTdpPhase.Unresponsive, controller.Phase);
        Assert.Equal("unresponsive", decisions[^1].Reason);
    }

    [Fact]
    public void AnUnresponsiveHoldEndsAsSoonAsDeliveryRecovers()
    {
        var controller = Started(15);
        Run(controller, UnansweredChainWindows, Late);

        var recovered = Run(controller, 1, Capped);

        Assert.Equal("unresponsive-ended", recovered[^1].Reason);
        Assert.Equal(AutoTdpPhase.Tracking, controller.Phase);
        Assert.Equal(27, controller.Watts);
    }

    [Fact]
    public void ContinuedMissesKeepRaisingUntilTheDeviceMaximum()
    {
        var controller = Started(26);

        var decisions = Run(controller, 60, Late);

        Assert.Equal(30, controller.Watts);
        Assert.Contains(decisions, decision => decision.Reason == "at-maximum");
        Assert.All(decisions, decision => Assert.True(decision.Watts <= 30));
    }

    [Fact]
    public void SustainedMissesAtMaximumKeepCantReachUntilDeliveryRecovers()
    {
        var controller = Started(30);

        var decisions = Run(controller, 6, Late);

        Assert.All(decisions.Skip(AutoTdpController.SustainedMisses - 1), decision =>
        {
            Assert.Equal("at-maximum", decision.Reason);
            Assert.False(decision.RequiresWrite);
        });
        Assert.Equal("Can't Reach", ShellSession.AutoTdpActivity(true, Status(decisions[^1])));
        Assert.Equal("on-target", Run(controller, 1, Tight)[^1].Reason);
    }

    [Fact]
    public void MeetingTheDeadlineWithoutHeadroomNeitherRaisesNorProbes()
    {
        var controller = Started(15);

        var decisions = Run(controller, 40, Tight);

        Assert.All(decisions, decision =>
        {
            Assert.Equal(AutoTdpAction.Hold, decision.Action);
            Assert.Equal("on-target", decision.Reason);
        });
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void ASettledPeriodOfHeadroomProbesOneStepDown()
    {
        var controller = Started(15);

        var decisions = Run(controller, DwellWindows, Fast);

        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
        Assert.Equal("probe-down", decisions[^1].Reason);
        Assert.Equal(13, controller.Watts);
        Assert.Equal(15, controller.LastGood);
    }

    [Fact]
    public void ACappedGameAtItsCapDescendsButNeverClimbs()
    {
        var controller = Started(20);

        var decisions = Run(controller, DwellWindows, Capped);

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Raise);
        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
        Assert.Equal(18, controller.Watts);
    }

    [Fact]
    public void ASingleHitchInsideTheDwellDoesNotPostponeTheProbe()
    {
        var controller = Started(15);

        // One late window among the settled ones is a hitch, not a reason to start over.
        Run(controller, 4, Fast);
        Run(controller, 1, Late);
        var decisions = Run(controller, DwellWindows - 4, Fast);

        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
    }

    [Fact]
    public void TwoHitchesInsideTheDwellDoPostponeTheProbe()
    {
        var controller = Started(15);

        Run(controller, 3, Fast);
        Run(controller, 1, Late);
        Run(controller, 3, Fast);
        Run(controller, 1, Late);
        var decisions = Run(controller, DwellWindows - 4, Fast);

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Probe);
    }

    [Fact]
    public void AProbeThatKeepsDeliveringBecomesTheNewOperatingPoint()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);

        var decisions = Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Fast);

        Assert.Equal("probe-accepted", decisions[^1].Reason);
        Assert.Equal(AutoTdpPhase.Tracking, controller.Phase);
        Assert.Equal(13, controller.Watts);
        Assert.Equal(13, controller.LastGood);
    }

    [Fact]
    public void SuccessfulProbesKeepDescendingUntilTheMinimumAndHoldThere()
    {
        var controller = Started(20);

        var decisions = Run(controller, 60, Capped);

        // Each accepted probe doubles the next step and needs no new dwell; no probe gives up more
        // than a third of the limit.
        Assert.Equal(
            new[] { 18, 14, 10, 8 },
            decisions.Where(decision => decision.Action is AutoTdpAction.Probe)
                .Select(decision => decision.Watts));
        Assert.Equal(8, controller.Watts);
        Assert.All(decisions.TakeLast(10), decision =>
        {
            Assert.Equal("at-minimum", decision.Reason);
            Assert.False(decision.RequiresWrite);
        });
    }

    [Fact]
    public void OneLateWindowDoesNotFailAProbe()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);
        Run(controller, AutoTdpController.SettleWindows, Fast);

        Run(controller, 1, Late);
        var decisions = Run(controller, AutoTdpController.ProbeWindows - 1, Fast);

        Assert.Equal("probe-accepted", decisions[^1].Reason);
        Assert.Equal(13, controller.Watts);
    }

    [Fact]
    public void TwoLateWindowsInThreeFailAProbeAndRestoreTheLastGoodLimit()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);
        Run(controller, AutoTdpController.SettleWindows, Fast);

        var decisions = Run(controller, 2, Late);

        Assert.Equal(AutoTdpAction.Restore, decisions[^1].Action);
        Assert.Equal("probe-failed", decisions[^1].Reason);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void AFailedProbeIsRetriedAfterTheBackoffRatherThanForbidden()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);
        Run(controller, AutoTdpController.SettleWindows, Fast);
        Run(controller, 2, Late);

        // The dwell is doubled, not closed: the same step is offered again once it is served.
        Run(controller, AutoTdpController.SettleWindows, Fast);
        var early = Run(controller, DwellWindows, Fast);
        var later = Run(controller, DwellWindows, Fast);

        Assert.DoesNotContain(early, decision => decision.Action is AutoTdpAction.Probe);
        Assert.Equal(AutoTdpAction.Probe, later[^1].Action);
        Assert.Equal(13, controller.Watts);
    }

    [Fact]
    public void ARaiseClearsTheBackoffBecauseTheOperatingPointChanged()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);
        Run(controller, AutoTdpController.SettleWindows, Fast);
        Run(controller, 2, Late);
        Run(controller, AutoTdpController.SettleWindows, Fast);

        // Raising moves the operating point, so the doubled dwell earned at the old one is gone.
        Run(controller, AutoTdpController.SustainedMisses, Late);
        Run(controller, AutoTdpController.SettleWindows + RaiseJudgeWindows, Fast);
        var decisions = Run(controller, DwellWindows, Fast);

        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
        Assert.Equal(21, controller.Watts);
    }

    [Fact]
    public void TheLoadLevelNeverSizesAProbe()
    {
        var idle = Started(24);
        var busy = Started(24);

        // Utilization is read under the current power envelope, so its level says nothing a
        // threshold could use. Half busy or nearly saturated, the first probe is one step.
        Run(idle, DwellWindows, Capped, 50.0);
        Run(busy, DwellWindows, Capped, 95.0);

        Assert.Equal(22, idle.Watts);
        Assert.Equal(22, busy.Watts);
    }

    [Fact]
    public void LoadThatHoldsStillAcrossAProbeLetsTheDescentGrow()
    {
        var controller = Started(24);
        Run(controller, DwellWindows, Capped, 50.0);
        Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Capped, 52.0);

        var next = Run(controller, 1, Capped, 52.0);

        Assert.Equal(AutoTdpAction.Probe, next[^1].Action);
        Assert.Equal(18, controller.Watts);
    }

    [Fact]
    public void GpuLoadThatClimbsAcrossAProbeSlowsTheDescent()
    {
        var controller = Started(24);
        Run(controller, DwellWindows, Capped, 50.0);
        Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Capped, 50.0);
        Run(controller, 1, Capped, 50.0);

        // The two-step probe held its frames, but the GPU worked 15 points harder for them. The
        // next step halves instead of doubling.
        Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Capped, 65.0);
        var next = Run(controller, 1, Capped, 65.0);

        Assert.Equal(AutoTdpAction.Probe, next[^1].Action);
        Assert.Equal(16, controller.Watts);
    }

    [Fact]
    public void CpuLoadThatClimbsAcrossAProbeSlowsTheDescentToo()
    {
        var controller = Started(24);
        Run(controller, WithCpu(DwellWindows, 40.0));
        Run(controller, WithCpu(AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, 40.0));
        Run(controller, WithCpu(1, 40.0));

        Run(controller, WithCpu(AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, 55.0));
        var next = Run(controller, WithCpu(1, 55.0));

        Assert.Equal(AutoTdpAction.Probe, next[^1].Action);
        Assert.Equal(16, controller.Watts);
    }

    [Fact]
    public void AProbeThatWentTooDeepRetriesHalfwayWithoutADwell()
    {
        var controller = Started(24);
        Run(controller, DwellWindows, Capped);
        Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Capped);
        Run(controller, 1, Capped);
        Run(controller, AutoTdpController.SettleWindows, Capped);

        // 22 W delivered, 18 W did not. Half the distance is 20 W, tried straight away.
        var failed = Run(controller, 2, Late);
        Run(controller, AutoTdpController.SettleWindows, Capped);
        var retried = Run(controller, 1, Capped);

        Assert.Equal(AutoTdpAction.Restore, failed[^1].Action);
        Assert.Equal(22, failed[^1].Watts);
        Assert.Equal(AutoTdpAction.Probe, retried[^1].Action);
        Assert.Equal(20, controller.Watts);
    }

    [Fact]
    public void AMissAfterAnAcceptedProbeEndsTheDescent()
    {
        var controller = Started(20);
        Run(controller, DwellWindows, Capped);
        Run(controller, AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows, Capped);

        Run(controller, 1, Late);
        var decisions = Run(controller, DwellWindows - 1, Capped);

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Probe);
        Assert.Equal(18, controller.Watts);
    }

    [Fact]
    public void ALongFinalFrameMakesTheWindowSevere()
    {
        var controller = Started(15);

        var decisions = Run(controller, AutoTdpReplay.Window(Capped, Target, Game, lastFrameMs: 300));

        Assert.Equal("quarantine-stall", decisions[^1].Reason);
        Assert.Equal(AutoTdpPhase.Quarantine, controller.Phase);
    }

    [Fact]
    public void AnOrdinaryHitchAtTheEndOfAWindowIsNotSevere()
    {
        var controller = Started(15);

        var decisions = Run(controller, AutoTdpReplay.Window(Capped, Target, Game, lastFrameMs: 185));

        Assert.NotEqual(AutoTdpPhase.Quarantine, controller.Phase);
        Assert.Equal("tracking-headroom", decisions[^1].Reason);
    }

    [Fact]
    public void AWindowFarPastItsDeadlineIsSevereEvenWhenItsLastFrameWasFast()
    {
        var controller = Started(15);

        // Four frames in a second, the last of them quick: the stall sat inside the window.
        var decisions = Run(controller, AutoTdpReplay.Window(254.0, Target, Game, lastFrameMs: 16.6));

        Assert.Equal(AutoTdpPhase.Quarantine, controller.Phase);
        Assert.Equal("quarantine-stall", decisions[^1].Reason);
    }

    [Fact]
    public void TheSameWindowReadTwiceIsNotCountedTwice()
    {
        var controller = Started(15);
        var window = AutoTdpReplay.Window(Late, Target, Game);
        Run(controller, window);

        // Ordinary tick phase drift re-reads the newest window. Counting it would raise power on
        // one late window read twice.
        var repeat = Run(controller, AutoTdpReplay.Repeat(window, 1032));
        var second = Run(controller, 1, Late);

        Assert.Equal(AutoTdpAction.Hold, repeat[^1].Action);
        Assert.Equal("window-repeat", repeat[^1].Reason);
        Assert.Equal(AutoTdpAction.Raise, second[^1].Action);
    }

    [Fact]
    public void AWindowThatWillNotCloseIsAHiatusBeforeItsOwnWindowExists()
    {
        var controller = Started(15);
        var window = AutoTdpReplay.Window(Capped, Target, Game);
        Run(controller, window);

        var decisions = Run(controller, AutoTdpReplay.Repeat(window, 1300));

        Assert.Equal("quarantine-hiatus", decisions[^1].Reason);
        Assert.Equal(AutoTdpPhase.Quarantine, controller.Phase);
    }

    [Fact]
    public void ARendererThatDisappearsDuringAStallStaysQuarantined()
    {
        var controller = Started(15);
        Run(controller, 1, Capped);

        // RTSS drops an entry once it is two seconds stale, so the stall outlives the telemetry and
        // the gap has to keep running on the controller's own clock.
        var first = Run(controller, AutoTdpReplay.Silent(Game, Target));
        var second = Run(controller, AutoTdpReplay.Silent(Game, Target));
        var third = Run(controller, AutoTdpReplay.Silent(Game, Target));

        Assert.Equal("no-telemetry", first[^1].Reason);
        Assert.Equal("quarantine-hiatus", second[^1].Reason);
        Assert.Equal("quarantine-hiatus", third[^1].Reason);
        Assert.Equal(AutoTdpPhase.Quarantine, controller.Phase);
    }

    [Fact]
    public void AQuarantineEndsOnThreeOrdinaryWindowsAndDiscardsTheirMisses()
    {
        var controller = Started(15);
        Run(controller, 1, Stalled);

        var recovery = Run(controller, 2, Late);
        var ended = Run(controller, 1, Late);
        var after = Run(controller, AutoTdpController.SustainedMisses - 1, Late);

        Assert.All(recovery, decision => Assert.Equal("quarantine-recovery", decision.Reason));
        Assert.Equal("quarantine-ended", ended[^1].Reason);
        Assert.All(after, decision => Assert.Equal(AutoTdpAction.Hold, decision.Action));
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void AStallDuringAProbeRestoresWithoutHoldingItAgainstTheStep()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);
        Run(controller, AutoTdpController.SettleWindows, Fast);

        var interrupted = Run(controller, 1, Stalled);
        Run(controller, 3, Fast);
        var retried = Run(controller, DwellWindows, Fast);

        Assert.Equal(AutoTdpAction.Restore, interrupted[^1].Action);
        Assert.Equal("probe-interrupted", interrupted[^1].Reason);
        Assert.Equal(AutoTdpAction.Probe, retried[^1].Action);
        Assert.Equal(13, controller.Watts);
    }

    [Fact]
    public void ALoadingStallNeverAddsPowerHoweverLongItLasts()
    {
        var controller = Started(15);

        // Long, and the GPU is doing nothing for it.
        var decisions = Run(controller, 30, Stalled, 12.0);

        Assert.All(decisions, decision => Assert.Equal(AutoTdpAction.Hold, decision.Action));
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void APersistentStallWithNoSensorIsReconsideredAsLoad()
    {
        var controller = Started(15);

        var decisions = Run(controller, PersistentStallWindows, Stalled);

        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal("stall-power-bound", decisions[^1].Reason);
        Assert.Equal(30, controller.Watts);
    }

    [Fact]
    public void LateFramesOnAnIdleGpuWaitBeforeCostingPower()
    {
        var controller = Started(15);

        var deferred = Run(controller, AutoTdpController.SustainedMisses + 5, Late, 20.0);
        var raised = Run(controller, 1, Late, 20.0);

        Assert.All(deferred.Skip(AutoTdpController.SustainedMisses - 1), decision =>
            Assert.Equal("miss-deferred", decision.Reason));
        Assert.Equal(AutoTdpAction.Raise, raised[^1].Action);
        Assert.Equal(23, controller.Watts);
    }

    [Fact]
    public void LateFramesOnABusyGpuAddPowerImmediately()
    {
        var controller = Started(15);

        var decisions = Run(controller, AutoTdpController.SustainedMisses, Late, 95.0);

        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal(23, controller.Watts);
    }

    [Fact]
    public void AProbeFailureOnAnIdleGpuDoesNotLengthenTheBackoff()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast, 20.0);
        Run(controller, AutoTdpController.SettleWindows, Fast, 20.0);
        var failure = Run(controller, 2, Late, 20.0);

        Run(controller, AutoTdpController.SettleWindows, Fast, 20.0);
        var retried = Run(controller, DwellWindows, Fast, 20.0);

        Assert.Equal("probe-inconclusive", failure[^1].Reason);
        Assert.Contains(retried, decision => decision.Action is AutoTdpAction.Probe);
    }

    [Fact]
    public void MissingTelemetryNeverCountsAsHeadroom()
    {
        var controller = Started(15);
        Run(controller, DwellWindows - 1, Fast);

        // The gap contributes no window time of its own, so it cannot complete the dwell.
        var silent = Run(controller, AutoTdpReplay.Silent(Game, Target));
        var next = Run(controller, 1, Fast);

        Assert.Equal("no-telemetry", silent[^1].Reason);
        Assert.Equal(AutoTdpAction.Probe, next[^1].Action);
        Assert.Equal(13, controller.Watts);
    }

    [Fact]
    public void AContextChangeDiscardsTheEvidenceGatheredForThePreviousOne()
    {
        var controller = Started(15);
        Run(controller, AutoTdpController.SustainedMisses - 1, Late);

        var decisions = Run(controller, AutoTdpReplay.Window(Late, Target, "steam:220|16.60ms"));

        Assert.Equal("context-changed", decisions[^1].Reason);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void ChangingTheFrameCapIsANewContext()
    {
        var controller = Started(15);
        Run(controller, AutoTdpController.SustainedMisses - 1, Late);

        var decisions = Run(controller, AutoTdpReplay.Window(8.0, 8.3, "steam:70|8.30ms"));

        Assert.Equal("context-changed", decisions[^1].Reason);
    }

    [Fact]
    public void AManualChangeSuspendsControlAndAutomaticControlDoesNotResumeItself()
    {
        var controller = Started(15);
        controller.PauseForManualChange(22);

        var decisions = Run(controller, 30, Late);

        Assert.All(decisions, decision => Assert.Equal("paused-manual", decision.Reason));
        Assert.True(controller.IsPaused);
        Assert.Equal(22, controller.Watts);
    }

    [Fact]
    public void SwitchingAutoTdpOffAndOnReturnsControlFromTheManualValue()
    {
        var controller = Started(15);
        controller.PauseForManualChange(22);

        controller.Start(22, Limits, Game);
        var decisions = Run(controller, AutoTdpController.SustainedMisses, Late);

        Assert.False(controller.IsPaused);
        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal(30, controller.Watts);
    }

    [Fact]
    public void StartingTakesTheLimitTheHardwareReports()
    {
        var controller = Started(15);
        Run(controller, DwellWindows, Fast);

        // A device found at 30 W is controlled from 30 W, whatever this context did before.
        Assert.Equal(30, controller.Start(30, Limits, Game));
        Assert.Equal(30, controller.Watts);
    }

    [Fact]
    public void StoppingRestoresTheLimitAutoTdpTookOverFrom()
    {
        var controller = Started(21);

        var decision = controller.Stop(15);

        Assert.Equal(AutoTdpAction.Release, decision.Action);
        Assert.Equal(15, decision.Watts);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void UnusableDeviceBoundsProduceNoWrites()
    {
        AutoTdpController controller = new();
        AutoTdpLimits unusable = new(0, 0, 0);
        controller.Start(15, unusable, Game);

        var decisions = AutoTdpReplay.Run(
            controller,
            unusable,
            AutoTdpReplay.Run(30, Late, Target, Game));

        Assert.All(decisions, decision =>
        {
            Assert.False(decision.RequiresWrite);
            Assert.Equal("limits-unusable", decision.Reason);
        });
    }

    [Fact]
    public void AWriteIsFollowedBySettlingBeforeMoreEvidenceIsCounted()
    {
        var controller = Started(15);
        Run(controller, AutoTdpController.SustainedMisses, Late);

        var decisions = Run(controller, AutoTdpController.SettleWindows, Late);

        Assert.All(decisions, decision =>
        {
            Assert.Equal(AutoTdpAction.Hold, decision.Action);
            Assert.Equal("settling", decision.Reason);
        });
        Assert.Equal(23, controller.Watts);
    }

    private static AutoTdpStatus Status(AutoTdpDecision decision)
    {
        return new AutoTdpStatus(
            AutoTdpState.Controlling,
            decision.Watts,
            Late,
            Target,
            Game,
            decision.Reason,
            decision.Action);
    }

    private static AutoTdpController Started(int watts)
    {
        AutoTdpController controller = new();
        controller.Start(watts, Limits, Game);
        return controller;
    }

    private static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        int count,
        double frametimeMs,
        double? gpuLoadPercent = null)
    {
        return AutoTdpReplay.Run(
            controller,
            Limits,
            AutoTdpReplay.Run(count, frametimeMs, Target, Game, gpuLoadPercent));
    }

    private static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        params AutoTdpObservation[] observations)
    {
        return AutoTdpReplay.Run(controller, Limits, observations);
    }

    /// <summary>Capped windows that carry a CPU reading and no GPU reading.</summary>
    private static AutoTdpObservation[] WithCpu(int count, double cpuLoadPercent)
    {
        return
        [
            .. AutoTdpReplay.Run(count, Capped, Target, Game)
                .Select(observation => observation with { CpuLoadPercent = cpuLoadPercent })
        ];
    }
}
