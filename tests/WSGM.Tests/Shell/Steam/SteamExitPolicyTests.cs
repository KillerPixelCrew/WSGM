using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamExitPolicyTests
{
    [Theory]
    // Game mode keeps Steam on screen: relaunch it, or open the only surface that is left.
    [InlineData(true, true, SteamExitReaction.RelaunchBigPicture)]
    [InlineData(true, false, SteamExitReaction.ShowOverlay)]
    // A desktop session has Explorer, so an exit never interrupts the user.
    [InlineData(false, true, SteamExitReaction.RelaunchDesktop)]
    [InlineData(false, false, SteamExitReaction.Ignore)]
    public void TheReactionFollowsTheSessionMode(
        bool inGameMode, bool autoRelaunch, SteamExitReaction expected)
    {
        Assert.Equal(expected, SteamExitPolicy.Decide(
            inGameMode, autoRelaunch, false, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExplicitCloseIsNeverUndone(bool inGameMode)
    {
        Assert.Equal(SteamExitReaction.Ignore, SteamExitPolicy.Decide(
            inGameMode, true, false, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATransitionInFlightOwnsSteamItself(bool inGameMode)
    {
        Assert.Equal(SteamExitReaction.Ignore, SteamExitPolicy.Decide(
            inGameMode, true, true, false));
    }
}
