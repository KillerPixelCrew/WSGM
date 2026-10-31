using System.Globalization;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Modern Standby wake decisions and the diagnostics read beside them.</summary>
public sealed class ModernStandbyTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    private static ModernStandbyDecision Decide(
        bool enabled = true,
        bool unattended = true,
        bool displayOn = false,
        double sinceWakeSeconds = 60,
        double sinceInputSeconds = 600,
        int attempts = 0)
    {
        return ModernStandbyPolicy.Decide(
            enabled,
            unattended,
            displayOn,
            TimeSpan.FromSeconds(sinceWakeSeconds),
            TimeSpan.FromSeconds(sinceInputSeconds),
            Grace,
            attempts);
    }

    [Fact]
    public void AnUnexplainedWakeOnADarkIdleMachineGoesBackToSleep()
    {
        var decision = Decide();

        Assert.Equal(ModernStandbyOutcome.Resuspend, decision.Outcome);
        Assert.True(decision.ShouldResuspend);
    }

    [Fact]
    public void TheModeDoesNothingUntilItIsSwitchedOn()
    {
        Assert.Equal(ModernStandbyOutcome.Disabled, Decide(false).Outcome);
    }

    [Fact]
    public void AWakeWindowsAttributesToAPersonIsNeverUndone()
    {
        Assert.Equal(ModernStandbyOutcome.UserWoke, Decide(unattended: false).Outcome);
    }

    [Fact]
    public void ALitDisplayStopsItRegardlessOfEverythingElse()
    {
        // The gate that does not depend on Windows counting a device as input: a gamepad does not
        // advance the last-input time, so a player holding a controller reads as idle. Suspending
        // the machine under their hands is the one failure this must never produce.
        var decision = Decide(displayOn: true, sinceInputSeconds: 100_000);

        Assert.Equal(ModernStandbyOutcome.DisplayOn, decision.Outcome);
        Assert.False(decision.ShouldResuspend);
        Assert.False(decision.ShouldKeepWatching);
    }

    [Fact]
    public void InputSinceTheWakeMeansSomebodyIsThere()
    {
        Assert.Equal(
            ModernStandbyOutcome.UserActive,
            Decide(sinceWakeSeconds: 60, sinceInputSeconds: 30).Outcome);
    }

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
        var decision = Decide(sinceWakeSeconds: 5, sinceInputSeconds: 600);

        Assert.Equal(ModernStandbyOutcome.TooSoon, decision.Outcome);
        Assert.False(decision.ShouldResuspend);
        Assert.True(decision.ShouldKeepWatching);
    }

    [Fact]
    public void OnlyTheGracePeriodIsWorthWaitingThrough()
    {
        // Every other refusal is a fact about this wake that will not change while it lasts, so a
        // timer that kept polling past one could never reach a decision.
        foreach (var decision in new[]
                 {
                     Decide(false),
                     Decide(unattended: false),
                     Decide(displayOn: true),
                     Decide(sinceInputSeconds: 1),
                     Decide(attempts: ModernStandbyPolicy.MaximumAttemptsPerWake)
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
        for (var attempt = 0; attempt < ModernStandbyPolicy.MaximumAttemptsPerWake; attempt++)
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
    {
        Assert.Equal(TimeSpan.FromSeconds(20), ModernStandbyPolicy.DefaultGrace);
    }

    // The standby diagnostic's own rules. It reads the live machine, so what is asserted here is the
    // shape of what it may say — never a value that depends on how this machine happens to be sleeping.
    [Theory]
    [InlineData(0, 0, "seconds")]
    [InlineData(45, 45, "seconds")]
    [InlineData(90, 2, "minutes")]
    [InlineData(3599, 60, "minutes")]
    [InlineData(3600, 1.0, "hours")]
    [InlineData(81801, 22.7, "hours")]
    public void ADurationIsDescribedInTheUnitSomeoneWouldSayItIn(int seconds, double value, string unit)
    {
        // The number is formatted for the user's culture, so the expectation is built the same way
        // rather than assuming a decimal point — this box runs in German, where it is a comma.
        // The last case is the reference handheld's measured 22h43m standby.
        var expected = unit == "hours"
            ? string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {unit}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0} {unit}");

        Assert.Equal(expected, ModernStandbyDiagnostics.Describe(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ReadingNeverThrowsAndAlwaysSaysSomething()
    {
        // A diagnostic that could throw would take the settings page with it, and one that could
        // return nothing would leave a blank row that reads as a bug.
        var report = ModernStandbyDiagnostics.Read();

        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
        Assert.NotNull(report.ArmedWakeSources);
    }

    [Fact]
    public void AnUnsupportedMachineIsToldSoRatherThanOfferedTheFeature()
    {
        // "Degrade safely on machines that do not support Modern Standby" is only honest if the
        // machine is told; a supported one must never carry the unsupported wording.
        var report = ModernStandbyDiagnostics.Read();

        if (report.Supported)
        {
            Assert.DoesNotContain("does not report Modern Standby", report.Summary, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(report.ArmedWakeSources);
        }
    }

    [Fact]
    public void TheSummaryNeverNamesACauseForTheWake()
    {
        // Windows exposes no documented call for what woke the machine. Naming one would be a guess
        // presented as a diagnosis, which is the specific thing this text refuses to do.
        var report = ModernStandbyDiagnostics.Read();

        Assert.DoesNotContain("woken by", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caused by", report.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
