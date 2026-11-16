using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>One action or dismissal per physical wake, whatever signals the wake brings.</summary>
public sealed class PhysicalWakePolicyTests
{
    [Fact]
    public void AResumeAfterARealSleepDismissesOnce()
    {
        var policy = new PhysicalWakePolicy();

        policy.NoteSuspendAccepted();

        Assert.True(policy.NoteResume());
        Assert.False(policy.NoteResume());
    }

    [Fact]
    public void TheStaleHalfOfAModernStandbyWakeDismissesNothing()
    {
        var policy = new PhysicalWakePolicy();

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
        var policy = new PhysicalWakePolicy();

        policy.NoteDisplayOff();

        Assert.True(policy.NoteDisplayOn());
        Assert.False(policy.NoteDisplayOn());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWakeThatBringsBothSignalsDismissesOnce(bool resumeFirst)
    {
        var policy = new PhysicalWakePolicy();
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
        var policy = new PhysicalWakePolicy();

        Assert.False(policy.NoteDisplayOn());
        Assert.False(policy.NoteResume());
    }

    [Fact]
    public void EveryLaterWakeDismissesAgain()
    {
        var policy = new PhysicalWakePolicy();

        policy.NoteDisplayOff();
        Assert.True(policy.NoteDisplayOn());
        policy.NoteSuspendAccepted();
        Assert.True(policy.NoteResume());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnattendedResumeLeavesTheWakePendingForThePerson(bool displayFirst)
    {
        var policy = new PhysicalWakePolicy();
        policy.NoteSuspendAccepted();

        Assert.False(policy.NoteResume(unattended: true));
        Assert.False(policy.NoteResume(unattended: true));
        Assert.True(displayFirst ? policy.NoteDisplayOn() : policy.NoteResume());
        Assert.False(policy.NoteDisplayOn());
        Assert.False(policy.NoteResume());
    }
}
