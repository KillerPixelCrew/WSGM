using WSGM.Core;

namespace WSGM.Tests;

public sealed class ModernStandbyPolicyTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    private static ModernStandbyDecision Decide(
        bool enabled = true,
        bool unattended = true,
        bool displayOn = false,
        double sinceWakeSeconds = 60,
        double sinceInputSeconds = 600,
        int attempts = 0)
        => ModernStandbyPolicy.Decide(
            enabled,
            unattended,
            displayOn,
            TimeSpan.FromSeconds(sinceWakeSeconds),
            TimeSpan.FromSeconds(sinceInputSeconds),
            Grace,
            attempts);

    [Fact]
    public void AnUnexplainedWakeOnADarkIdleMachineGoesBackToSleep()
    {
        ModernStandbyDecision decision = Decide();

        Assert.Equal(ModernStandbyOutcome.Resuspend, decision.Outcome);
        Assert.True(decision.ShouldResuspend);
    }

    [Fact]
    public void TheModeDoesNothingUntilItIsSwitchedOn()
        => Assert.Equal(ModernStandbyOutcome.Disabled, Decide(enabled: false).Outcome);

    [Fact]
    public void AWakeWindowsAttributesToAPersonIsNeverUndone()
        => Assert.Equal(ModernStandbyOutcome.UserWoke, Decide(unattended: false).Outcome);

    [Fact]
    public void ALitDisplayStopsItRegardlessOfEverythingElse()
    {
        // The gate that does not depend on Windows counting a device as input: a gamepad does not
        // advance the last-input time, so a player holding a controller reads as idle. Suspending
        // the machine under their hands is the one failure this must never produce.
        ModernStandbyDecision decision = Decide(displayOn: true, sinceInputSeconds: 100_000);

        Assert.Equal(ModernStandbyOutcome.DisplayOn, decision.Outcome);
        Assert.False(decision.ShouldResuspend);
        Assert.False(decision.ShouldKeepWatching);
    }

    [Fact]
    public void InputSinceTheWakeMeansSomebodyIsThere()
        => Assert.Equal(
            ModernStandbyOutcome.UserActive,
            Decide(sinceWakeSeconds: 60, sinceInputSeconds: 30).Outcome);

    [Fact]
    public void InputInsideTheGraceCountsEvenWhenTheWakeIsOlderStill()
    {
        // Older than the wake but only just: someone touched it a moment ago.
        Assert.Equal(
            ModernStandbyOutcome.UserActive,
            Decide(sinceWakeSeconds: 5, sinceInputSeconds: 10).Outcome);
    }

    [Fact]
    public void AWakeIsGivenTimeToSettleBeforeItIsActedOn()
    {
        ModernStandbyDecision decision = Decide(sinceWakeSeconds: 5, sinceInputSeconds: 600);

        Assert.Equal(ModernStandbyOutcome.TooSoon, decision.Outcome);
        Assert.False(decision.ShouldResuspend);
        Assert.True(decision.ShouldKeepWatching);
    }

    [Fact]
    public void OnlyTheGracePeriodIsWorthWaitingThrough()
    {
        // Every other refusal is a fact about this wake that will not change while it lasts, so a
        // timer that kept polling past one could never reach a decision.
        foreach (ModernStandbyDecision decision in new[]
        {
            Decide(enabled: false),
            Decide(unattended: false),
            Decide(displayOn: true),
            Decide(sinceInputSeconds: 1),
            Decide(attempts: ModernStandbyPolicy.MaximumAttemptsPerWake),
        })
        {
            Assert.False(decision.ShouldKeepWatching);
            Assert.False(decision.ShouldResuspend);
        }
    }

    [Fact]
    public void AWakeIsOnlySleptThroughABoundedNumberOfTimes()
    {
        // A machine waking for a reason WSGM cannot see would otherwise be suspended in a loop the
        // user cannot escape, which is worse than the drain this exists to stop.
        for (int attempt = 0; attempt < ModernStandbyPolicy.MaximumAttemptsPerWake; attempt++)
        {
            Assert.Equal(ModernStandbyOutcome.Resuspend, Decide(attempts: attempt).Outcome);
        }

        Assert.Equal(
            ModernStandbyOutcome.AttemptsExhausted,
            Decide(attempts: ModernStandbyPolicy.MaximumAttemptsPerWake).Outcome);
    }

    [Fact]
    public void TheUserWokeItAnswerOutranksEveryOtherReason()
    {
        // Ordering matters: a dark, idle, attended wake is still attended. The user pressed the
        // power button and walked back to it; the screen has simply not come up yet.
        Assert.Equal(
            ModernStandbyOutcome.UserWoke,
            Decide(unattended: false, displayOn: false, sinceInputSeconds: 100_000).Outcome);
    }

    [Fact]
    public void TheDefaultGraceIsTheOneThePolicyPublishes()
        => Assert.Equal(TimeSpan.FromSeconds(20), ModernStandbyPolicy.DefaultGrace);
}
