using WSGM.Core;

namespace WSGM.Tests;

public sealed class GameplayDisplayHoldTests
{
    [Fact]
    public void AGameRunningHoldsTheDisplayAndStoppingReleasesIt()
    {
        using GameplayDisplayHold hold = new();
        Assert.False(hold.IsHeld);

        hold.Apply(gameRunning: true);
        Assert.True(hold.IsHeld);

        hold.Apply(gameRunning: false);
        Assert.False(hold.IsHeld);
    }

    [Fact]
    public void RepeatedObservationsOfTheSameStateAreIdempotent()
    {
        // The running-application snapshot is republished on every change of anything in it, most
        // of which is not whether a game is up. Re-acquiring a held request each time would be a
        // Windows call per publication for no change in state.
        using GameplayDisplayHold hold = new();

        hold.Apply(gameRunning: true);
        hold.Apply(gameRunning: true);
        Assert.True(hold.IsHeld);

        hold.Apply(gameRunning: false);
        hold.Apply(gameRunning: false);
        Assert.False(hold.IsHeld);
    }

    [Fact]
    public void TheHoldNeverOutlivesTheSession()
    {
        // A session torn down mid-game must not leave Windows holding the display for a process
        // that no longer exists, which is exactly how a scoped hold becomes a permanent one.
        GameplayDisplayHold hold = new();
        hold.Apply(gameRunning: true);
        Assert.True(hold.IsHeld);

        hold.Dispose();

        Assert.False(hold.IsHeld);
    }

    [Fact]
    public void ApplyingAfterDisposalNeverReacquires()
    {
        GameplayDisplayHold hold = new();
        hold.Dispose();

        hold.Apply(gameRunning: true);

        Assert.False(hold.IsHeld);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        GameplayDisplayHold hold = new();
        hold.Apply(gameRunning: true);

        hold.Dispose();
        hold.Dispose();

        Assert.False(hold.IsHeld);
    }
}
