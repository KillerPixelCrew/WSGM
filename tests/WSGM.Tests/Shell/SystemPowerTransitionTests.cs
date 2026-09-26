using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The rule that keeps a modern standby wake from quiescing a machine that is already awake.
/// </summary>
/// <remarks>
///     A handheld resumes its hibernation image into S0 idle and leaves it again about a second
///     later, so Windows delivers a resume, a suspend and a second resume within a few hundred
///     milliseconds of one wake. The Claw's log recorded that on all six wakes across 2026-09-25 and
///     26; each time the suspend in the middle removed the virtual controller and Steam took over
///     three minutes to find its replacement.
/// </remarks>
public sealed class SystemPowerTransitionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(1999)]
    public void ASuspendArrivingRightAfterAWakeIsTheStandbyWindowThatAlreadyEnded(double sinceResumeMs)
    {
        Assert.True(ShellSession.IsSuspendContradictedByResume(sinceResumeMs));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(3600_000)]
    public void ASuspendThatFollowsOrdinaryUseIsHonoured(double sinceResumeMs)
    {
        Assert.False(ShellSession.IsSuspendContradictedByResume(sinceResumeMs));
    }

    [Fact]
    public void ASessionThatNeverResumedTreatsItsFirstSuspendAsReal()
    {
        // The field starts an hour in the past rather than at zero, so a sleep in the first seconds
        // of a session is not mistaken for the tail of a wake that never happened.
        Assert.False(ShellSession.IsSuspendContradictedByResume(TimeSpan.FromHours(1).TotalMilliseconds));
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
