using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DesktopActionAdmissionTests
{
    [Fact]
    public void DuplicateEventsStaySuppressedDuringWorkAndCooldown()
    {
        DesktopActionAdmission admission = new();
        Assert.True(admission.TryBegin(false, false, 100));
        Assert.False(admission.TryBegin(false, false, 10000));
        admission.End();
        Assert.False(admission.TryBegin(false, false, 200));
        Assert.True(admission.TryBegin(false, false, 5100));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GameModeAndTransitionsSuppressDesktopActions(bool gameMode, bool transitioning)
    {
        DesktopActionAdmission admission = new();
        Assert.False(admission.TryBegin(gameMode, transitioning, 100));
        Assert.True(admission.TryBegin(false, false, 101));
    }
}
