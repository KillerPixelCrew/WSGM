using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The rule that keeps a modern standby wake from quiescing a machine that is already awake.
/// </summary>
/// <remarks>
///     A handheld resumes its hibernation image into S0 idle and leaves it again about a second
///     later, so Windows delivers a resume, a suspend and a second resume within a few hundred
///     milliseconds of one wake. The Claw's log recorded that on every wake across 2026-09-25 to 27;
///     each time the suspend in the middle removed the virtual controller and Steam took over three
///     minutes to find its replacement.
/// </remarks>
public sealed class SystemPowerTransitionTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(630)]
    [InlineData(1999)]
    public void ASuspendRightAfterAWakeIsTheWindowThatAlreadyEnded(double gapMs)
    {
        var gap = TimeSpan.FromMilliseconds(gapMs);

        Assert.True(ShellSession.IsStaleSuspend(gap, gap));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(3600_000)]
    public void ASuspendThatFollowsOrdinaryUseIsHonoured(double gapMs)
    {
        var gap = TimeSpan.FromMilliseconds(gapMs);

        Assert.False(ShellSession.IsStaleSuspend(gap, gap));
    }

    [Fact]
    public void OneClockJumpingForwardCannotHideAWakeThatJustHappened()
    {
        // A hibernation resume adjusts the performance counter, and an adjustment landing between the
        // resume and the suspend made a 630 ms gap measure as half an hour on the monotonic clock.
        Assert.True(ShellSession.IsStaleSuspend(Hour, TimeSpan.FromMilliseconds(630)));
        Assert.True(ShellSession.IsStaleSuspend(TimeSpan.FromMilliseconds(630), Hour));
    }

    [Fact]
    public void AClockCorrectedBackwardsIsIgnoredRatherThanDeciding()
    {
        // Windows rewrites the system time on a hibernation resume, so the wall clock can run
        // backwards across it. A negative gap is no evidence either way; the other clock decides.
        Assert.True(ShellSession.IsStaleSuspend(
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(-5000)));
        Assert.False(ShellSession.IsStaleSuspend(
            TimeSpan.FromMilliseconds(-5000),
            TimeSpan.FromMilliseconds(-5000)));
    }

    [Fact]
    public void ASessionThatNeverResumedTreatsItsFirstSuspendAsReal()
    {
        Assert.False(ShellSession.IsStaleSuspend(Hour, Hour));
    }
}
