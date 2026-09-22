using System;

namespace WSGM.PackagedLaunch;

/// <summary>Why the launcher stopped, and what Steam should make of it.</summary>
public enum GameSessionOutcome
{
    /// <summary>Still supervising.</summary>
    Running,

    /// <summary>The game ran and exited. Steam's running state is released normally.</summary>
    Completed,

    /// <summary>Nothing matching the package appeared within the settle window.</summary>
    NeverAppeared,

    /// <summary>The game ran, but the overlay or controller work it asked for did not.</summary>
    Degraded,

    /// <summary>The wrapper was asked to stop while the game was still running.</summary>
    Cancelled
}

/// <summary>What the supervisor knows at one moment.</summary>
/// <param name="SawGame">Whether a process carrying the package identity was ever seen.</param>
/// <param name="GameRunning">Whether one is running now.</param>
/// <param name="Elapsed">Time since activation returned.</param>
/// <param name="GoneFor">How long no game process has been seen, or null while one is running.</param>
/// <param name="Degraded">Whether the session could not do what the user asked.</param>
/// <param name="Cancelled">Whether a stop was requested.</param>
public readonly record struct GameSessionFacts(
    bool SawGame,
    bool GameRunning,
    TimeSpan Elapsed,
    TimeSpan? GoneFor,
    bool Degraded,
    bool Cancelled);

/// <summary>Decides when a supervised session is over.</summary>
/// <remarks>
///     <para>
///         Pure, because this is the decision that releases Steam's running state, and getting it
///         wrong in either direction is visible to the user: too eager and Steam says stopped while
///         the game is on screen, too reluctant and the shortcut stays lit after the game is gone.
///     </para>
///     <para>
///         The exit grace exists because a packaged game is not one process. A title can hand off
///         between its own binaries, and a helper can outlive the game briefly, so a single empty
///         observation is not an exit.
///     </para>
/// </remarks>
public static class GameSessionExitDecision
{
    /// <summary>How long the game must be gone before the session is over.</summary>
    public static TimeSpan ExitGrace { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the activated game to appear at all.</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromSeconds(90);

    /// <summary>Decides the session's outcome from what the supervisor currently knows.</summary>
    /// <param name="facts">What is known now.</param>
    /// <returns>The outcome, which is <see cref="GameSessionOutcome.Running" /> until it is over.</returns>
    public static GameSessionOutcome Decide(GameSessionFacts facts)
    {
        // A stop request outranks everything: the game is deliberately left running, and claiming a
        // completed session would tell Steam the user finished playing.
        if (facts.Cancelled)
        {
            return GameSessionOutcome.Cancelled;
        }

        if (facts.GameRunning)
        {
            return GameSessionOutcome.Running;
        }

        if (facts.SawGame)
        {
            return facts.GoneFor >= ExitGrace
                ? facts.Degraded ? GameSessionOutcome.Degraded : GameSessionOutcome.Completed
                : GameSessionOutcome.Running;
        }

        return facts.Elapsed >= Settle ? GameSessionOutcome.NeverAppeared : GameSessionOutcome.Running;
    }

    /// <summary>The process exit code one outcome reports.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    ///     Distinct codes so a pasted log says which of the failure shapes happened without needing
    ///     the transcript beside it.
    /// </remarks>
    public static int ExitCode(GameSessionOutcome outcome)
    {
        return outcome switch
        {
            GameSessionOutcome.Completed => 0,
            GameSessionOutcome.NeverAppeared => 1,
            GameSessionOutcome.Degraded => 5,
            GameSessionOutcome.Cancelled => 6,
            _ => 1
        };
    }
}
