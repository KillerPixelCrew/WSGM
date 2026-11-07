using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>One dismissal of Steam's screensaver per physical wake, whatever signals the wake brings.</summary>
public sealed class ScreensaverWakePolicyTests
{
    [Fact]
    public void AResumeAfterARealSleepDismissesOnce()
    {
        var policy = new ScreensaverWakePolicy();

        policy.NoteSuspendAccepted();

        Assert.True(policy.NoteResume());
        Assert.False(policy.NoteResume());
    }

    [Fact]
    public void TheStaleHalfOfAModernStandbyWakeDismissesNothing()
    {
        var policy = new ScreensaverWakePolicy();

        // Resume, a suspend the session drops as stale (never accepted), and a second resume.
        policy.NoteSuspendAccepted();
        var first = policy.NoteResume();
        var second = policy.NoteResume();

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void ADisplayWakeWithoutASleepDismissesOnce()
    {
        var policy = new ScreensaverWakePolicy();

        policy.NoteDisplayOff();

        Assert.True(policy.NoteDisplayOn());
        Assert.False(policy.NoteDisplayOn());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWakeThatBringsBothSignalsDismissesOnce(bool resumeFirst)
    {
        var policy = new ScreensaverWakePolicy();
        policy.NoteDisplayOff();
        policy.NoteSuspendAccepted();

        var dismissals = 0;
        if (resumeFirst)
        {
            dismissals += policy.NoteResume() ? 1 : 0;
            dismissals += policy.NoteDisplayOn() ? 1 : 0;
        }
        else
        {
            dismissals += policy.NoteDisplayOn() ? 1 : 0;
            dismissals += policy.NoteResume() ? 1 : 0;
        }

        Assert.Equal(expected: 1, actual: dismissals);
    }

    [Fact]
    public void ALitDisplayOrAResumeWithoutADarkPeriodDismissesNothing()
    {
        var policy = new ScreensaverWakePolicy();

        Assert.False(policy.NoteDisplayOn());
        Assert.False(policy.NoteResume());
    }

    [Fact]
    public void EveryLaterWakeDismissesAgain()
    {
        var policy = new ScreensaverWakePolicy();

        policy.NoteDisplayOff();
        Assert.True(policy.NoteDisplayOn());
        policy.NoteSuspendAccepted();
        Assert.True(policy.NoteResume());
    }
}
