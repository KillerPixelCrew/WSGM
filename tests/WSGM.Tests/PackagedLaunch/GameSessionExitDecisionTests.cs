using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     When a supervised session is over. This releases Steam's running state, and being wrong in
///     either direction is visible: too eager and Steam says stopped while the game is on screen,
///     too reluctant and the shortcut stays lit after the game is gone.
/// </summary>
public sealed class GameSessionExitDecisionTests
{
    private static GameSessionFacts Facts(
        bool sawGame = true,
        bool gameRunning = false,
        double elapsedSeconds = 30,
        double? goneForSeconds = null,
        bool degraded = false,
        bool cancelled = false)
    {
        return new GameSessionFacts(
            sawGame,
            gameRunning,
            TimeSpan.FromSeconds(elapsedSeconds),
            goneForSeconds is { } gone ? TimeSpan.FromSeconds(gone) : null,
            degraded,
            cancelled);
    }

    [Fact]
    public void ARunningGameKeepsTheSessionOpen()
    {
        Assert.Equal(GameSessionOutcome.Running,
            GameSessionExitDecision.Decide(Facts(gameRunning: true)));
    }

    [Fact]
    public void AGameGoneForLessThanTheGraceIsNotAnExit()
    {
        // A packaged title is not one process. It can hand off between its own binaries, so a
        // single empty observation is a handoff as often as it is an exit.
        Assert.Equal(GameSessionOutcome.Running,
            GameSessionExitDecision.Decide(Facts(goneForSeconds: 1)));
    }

    [Fact]
    public void AGameGoneForTheFullGraceEndsTheSession()
    {
        Assert.Equal(GameSessionOutcome.Completed, GameSessionExitDecision.Decide(
            Facts(goneForSeconds: GameSessionExitDecision.ExitGrace.TotalSeconds)));
    }

    [Fact]
    public void ADegradedSessionIsDistinguishedFromACompletedOne()
    {
        // A game that ran without its overlay is not the same outcome as one that ran with it, and
        // the exit code is the only place that difference survives.
        var outcome = GameSessionExitDecision.Decide(
            Facts(goneForSeconds: 10, degraded: true));

        Assert.Equal(GameSessionOutcome.Degraded, outcome);
        Assert.NotEqual(
            GameSessionExitDecision.ExitCode(GameSessionOutcome.Completed),
            GameSessionExitDecision.ExitCode(outcome));
    }

    [Fact]
    public void NothingAppearingWithinTheSettleWindowIsItsOwnOutcome()
    {
        var outcome = GameSessionExitDecision.Decide(Facts(
            false,
            elapsedSeconds: GameSessionExitDecision.Settle.TotalSeconds));

        Assert.Equal(GameSessionOutcome.NeverAppeared, outcome);
    }

    [Fact]
    public void AGameStillStartingKeepsTheSessionOpenUntilTheSettleWindowIsSpent()
    {
        Assert.Equal(GameSessionOutcome.Running,
            GameSessionExitDecision.Decide(Facts(false, elapsedSeconds: 5)));
    }

    [Fact]
    public void AStopRequestOutranksEverythingElse()
    {
        // The game is deliberately left running, so reporting a completed session would tell Steam
        // the user finished playing.
        var outcome = GameSessionExitDecision.Decide(
            Facts(gameRunning: true, cancelled: true));

        Assert.Equal(GameSessionOutcome.Cancelled, outcome);
        Assert.NotEqual(0, GameSessionExitDecision.ExitCode(outcome));
    }

    [Fact]
    public void OnlyACompletedSessionReportsSuccess()
    {
        Assert.Equal(0, GameSessionExitDecision.ExitCode(GameSessionOutcome.Completed));
        foreach (var outcome in new[]
                 {
                     GameSessionOutcome.NeverAppeared,
                     GameSessionOutcome.Degraded,
                     GameSessionOutcome.Cancelled
                 })
        {
            Assert.NotEqual(0, GameSessionExitDecision.ExitCode(outcome));
        }
    }
}
