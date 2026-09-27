using System;
using System.IO;
using System.Linq;
using System.Threading;
using WSGM.Core;

namespace WSGM.PackagedLaunch;

/// <summary>Starts another launcher's game and keeps Steam's running state for as long as it runs.</summary>
/// <remarks>
///     <para>
///         The Game Library points a title that launches through Epic, GOG Galaxy, Ubisoft Connect,
///         Battle.net, Amazon Games, Prism Launcher or ATLauncher at this. The program Steam starts
///         for such a title usually hands the request to a launcher that is already running and exits
///         at once, which Steam reads as the game stopping - and with the running state goes the
///         shortcut's Steam Input layout.
///     </para>
///     <para>
///         So this stays alive instead: it starts the launcher outside Steam's tree, finds the game by
///         where it runs from or by the instance its command line names, holds it in the kill-on-close
///         job so stopping the shortcut in Steam stops the game, and exits when the game does.
///         Nothing is injected into the game, and nothing is written into it.
///     </para>
///     <para>
///         The launcher it starts is held by its handle. A launcher that exits at once is ordinary -
///         it handed the request to a copy already running - so an exit alone ends nothing. One that
///         exits with a failure while no copy of it runs had nobody to hand the request to, and the
///         game cannot appear; waiting out the settle window for it would only keep Steam showing a
///         game that is not there.
///     </para>
/// </remarks>
internal static class FollowSession
{
    /// <summary>Runs one followed session.</summary>
    /// <param name="request">What to start and how to recognise the game.</param>
    /// <param name="cancellation">Requests a stop, leaving the game running.</param>
    /// <returns>The session's exit code.</returns>
    internal static int Run(PackagedFollowRequest request, CancellationToken cancellation)
    {
        PackagedLaunchLog.Info(
            $"Following a game started by {Path.GetFileName(request.Program)}"
            + $"{(request.Directory.Length > 0 ? ", installed in its own folder" : string.Empty)}"
            + $"{(request.Marker.Length > 0 ? ", recognised by its instance folder" : string.Empty)}.");

        FollowedGame game = new(request.Directory, request.Marker, request.MarkerImages, request.Program);
        using var launcher = DetachedStart.Start(request.Program, request.Arguments, out var detail);
        PackagedLaunchLog.Info(detail);
        if (launcher is null)
        {
            PackagedLaunchLog.Error(
                "The launcher did not start, so this wrapper is exiting rather than leaving Steam showing a "
                + "game that never started.");
            return Program.ExitActivationFailed;
        }

        using GameSessionJob job = new(true);
        GameSessionSupervisor supervisor = new(game, job, null, GameSessionTimings.Followed,
            () => LauncherFailed(launcher));
        var outcome = supervisor.Run(0, cancellation);
        if (outcome is GameSessionOutcome.Cancelled)
        {
            // The job is kill-on-close and is about to be closed. A cancellation leaves the game
            // running, so the containment is let go first.
            job.Abandon();
        }

        PackagedLaunchLog.Info(outcome switch
        {
            GameSessionOutcome.Completed => "The game exited; releasing Steam's running state.",
            GameSessionOutcome.NeverAppeared when LauncherFailed(launcher) =>
                $"{launcher.Name} exited with code {launcher.ExitCode()} and no copy of it is running, so the "
                + "game cannot start. Open the launcher to see what it needs, then start the game again.",
            GameSessionOutcome.NeverAppeared =>
                $"The game never appeared: nothing matched {game.Description} within "
                + $"{GameSessionTimings.Followed.Settle.TotalMinutes:0} minutes. Check that the launcher "
                + "started it, and that it is installed where the Game Library found it.",
            GameSessionOutcome.Cancelled => "Stop requested; leaving the game running and exiting.",
            _ => "The session ended."
        });
        return GameSessionExitDecision.ExitCode(outcome);
    }

    /// <summary>Whether the launcher failed with nobody to hand the game to.</summary>
    /// <remarks>
    ///     Both halves are needed. A zero exit is the ordinary handoff to a resident copy, and a
    ///     failure exit while a copy runs may still have reached it. The resident copy is found by the
    ///     program's file name in the process list, which opens no process.
    /// </remarks>
    private static bool LauncherFailed(StartedLauncher launcher)
    {
        return launcher.ExitCode() is { } code and not 0
               && !ProcessInspector.Snapshot().Any(entry =>
                   string.Equals(entry.Name, launcher.Name, StringComparison.OrdinalIgnoreCase));
    }
}
