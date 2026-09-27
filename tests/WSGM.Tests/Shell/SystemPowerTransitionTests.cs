using WSGM.Interop;
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
    public void ASuspendRightAfterAWakeThatNeverSuspendedIsTheWindowThatAlreadyEnded(double gapMs)
    {
        var gap = TimeSpan.FromMilliseconds(gapMs);

        Assert.True(ShellSession.IsSuspendContradictedByResume(true, gap, gap));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(3600_000)]
    public void ASuspendThatFollowsOrdinaryUseIsHonoured(double gapMs)
    {
        var gap = TimeSpan.FromMilliseconds(gapMs);

        Assert.False(ShellSession.IsSuspendContradictedByResume(true, gap, gap));
    }

    [Fact]
    public void AWakeWhoseSuspendThisProcessDidSeeNeverSuppressesTheNextOne()
    {
        // Without the unmatched-resume signature there is no reason to doubt the notification, however
        // close to the wake it lands.
        Assert.False(ShellSession.IsSuspendContradictedByResume(
            false,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void OneClockJumpingForwardCannotHideAWakeThatJustHappened()
    {
        // The first attempt at this guard measured only the monotonic clock and never fired once: a
        // hibernation resume adjusts the performance counter, and the adjustment landing between the
        // resume and the suspend made a 630 ms gap measure as half an hour.
        Assert.True(ShellSession.IsSuspendContradictedByResume(
            true,
            Hour,
            TimeSpan.FromMilliseconds(630)));
        Assert.True(ShellSession.IsSuspendContradictedByResume(
            true,
            TimeSpan.FromMilliseconds(630),
            Hour));
    }

    [Fact]
    public void AClockCorrectedBackwardsOnResumeDoesNotSuppressASuspend()
    {
        // Windows rewrites the system time on a hibernation resume, so the wall clock can run
        // backwards across it. A negative gap is not evidence of anything.
        Assert.False(ShellSession.IsSuspendContradictedByResume(
            true,
            TimeSpan.FromMilliseconds(-5000),
            TimeSpan.FromMilliseconds(-5000)));
    }

    [Fact]
    public void ASessionThatNeverResumedTreatsItsFirstSuspendAsReal()
    {
        Assert.False(ShellSession.IsSuspendContradictedByResume(false, Hour, Hour));
    }

    [Fact]
    public void EveryResumeCodeWindowsCanSendIsADistinctValue()
    {
        // PBT_APMRESUMECRITICAL is the one a process gets when it never saw the suspend, which on a
        // modern standby machine is how an ordinary hibernate ends.
        Assert.Equal(0x12, NativeMethods.PbtApmResumeAutomatic);
        Assert.Equal(0x7, NativeMethods.PbtApmResumeSuspend);
        Assert.Equal(0x6, NativeMethods.PbtApmResumeCritical);
        Assert.Equal(0x4, NativeMethods.PbtApmSuspend);
    }
}
