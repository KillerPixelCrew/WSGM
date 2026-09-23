using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>
///     Stays alive for the whole game session, so Steam keeps the shortcut in a running state, and
///     holds the game's processes in the kill-on-close job so stopping the shortcut stops the game.
/// </summary>
/// <remarks>
///     <para>
///         This is the launcher's primary job and the reason the whole feature works: package
///         activation puts the game outside Steam's launch tree, so Steam can only see the wrapper.
///     </para>
///     <para>
///         Discovery polls, because a packaged title's processes appear over several seconds and
///         Windows offers no notification a non-elevated process can rely on. Lifetime does not: once
///         a game process is contained, the job's own active count answers whether it is still
///         running, which is cheaper and more truthful than re-enumerating the machine. The poll
///         therefore slows down once the game is established.
///     </para>
/// </remarks>
internal sealed class GameSessionSupervisor(
    string packageFamilyName,
    GameSessionJob job,
    Action<ProcessFacts>? onGameProcess = null)
{
    /// <summary>How often to look for new processes while the game is still starting.</summary>
    private static readonly TimeSpan DiscoveryPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>How often to check a settled session, which only has to notice an exit.</summary>
    private static readonly TimeSpan SettledPoll = TimeSpan.FromSeconds(2);

    /// <summary>How long a game stays in discovery after its first process appears.</summary>
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(30);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly HashSet<int> _known = [];

    /// <summary>How many of the processes seen the job actually accepted.</summary>
    /// <remarks>
    ///     Assignment is allowed to fail. A job that holds only some of the game's processes
    ///     reports zero active as soon as the ones it does hold exit, which looks exactly like the
    ///     game ending; the wrapper would then release Steam while the game is still running. Only
    ///     a job that holds everything seen can be trusted to answer that question.
    /// </remarks>
    private int _containedCount;

    /// <summary>Whether the session could not do what the user asked of it.</summary>
    internal bool Degraded { get; set; }

    /// <summary>Supervises until the game exits, never appears, or a stop is requested.</summary>
    /// <param name="seedProcessId">The process activation returned.</param>
    /// <param name="cancellationToken">Requests a stop, leaving the game running.</param>
    /// <returns>Why the session ended.</returns>
    internal GameSessionOutcome Run(int seedProcessId, CancellationToken cancellationToken)
    {
        var sawGame = false;
        TimeSpan? goneSince = null;
        TimeSpan? firstSeen = null;

        while (true)
        {
            var discoveryOpen = firstSeen is null || _clock.Elapsed - firstSeen < DiscoveryWindow;
            var running = Observe(seedProcessId, discoveryOpen, ref sawGame, ref firstSeen);
            if (running)
            {
                goneSince = null;
            }
            else if (sawGame)
            {
                goneSince ??= _clock.Elapsed;
            }

            var outcome = GameSessionExitDecision.Decide(new GameSessionFacts(
                sawGame,
                running,
                _clock.Elapsed,
                goneSince is { } gone ? _clock.Elapsed - gone : null,
                Degraded,
                cancellationToken.IsCancellationRequested));
            if (outcome is not GameSessionOutcome.Running)
            {
                return outcome;
            }

            var discovering = firstSeen is null || _clock.Elapsed - firstSeen < DiscoveryWindow;
            cancellationToken.WaitHandle.WaitOne(discovering ? DiscoveryPoll : SettledPoll);
        }
    }

    /// <summary>Whether any of the game's processes is running, contained and reported as it appears.</summary>
    /// <param name="seedProcessId">The process activation returned.</param>
    /// <param name="discoveryOpen">Whether the discovery window is still open.</param>
    /// <param name="sawGame">Whether a process carrying the game's identity has ever been seen.</param>
    /// <param name="firstSeen">When the first one appeared.</param>
    /// <returns>Whether the game is running.</returns>
    private bool Observe(
        int seedProcessId, bool discoveryOpen, ref bool sawGame, ref TimeSpan? firstSeen)
    {
        // Once the game is established the kernel's own count answers this, and the machine does not
        // have to be enumerated at all. Not while discovery is open, though: the GDK path's first
        // match is the launch helper, and the real game appears after it. Returning early there
        // would mean the game itself is never found, never contained, and the helper's exit alone
        // releases Steam while the game is still running.
        if (sawGame && !discoveryOpen && job.ActiveProcesses() is { } active)
        {
            if (active > 0)
            {
                return true;
            }

            // Zero active processes is the normal exit only if the job holds everything that was
            // seen. A refused assignment — legal, and normal for a packaged app already in a
            // system job — leaves a running process the kernel is not counting, so its absence
            // from the count proves nothing and the machine is looked at instead.
            if (_containedCount == _known.Count)
            {
                return false;
            }
        }

        var snapshot = ProcessInspector.Snapshot();
        var running = false;
        foreach (var entry in snapshot)
        {
            if (entry.Id <= 4 || entry.Id == Environment.ProcessId)
            {
                continue;
            }

            // The seed is the game's own process for a UWP title and its launch helper for a GDK
            // one, so it is admitted by identity like everything else rather than by being the seed.
            if (ProcessInspector.PackageFamilyNameOf(entry.Id) is not { } family
                || !string.Equals(family, packageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProcessInspector.Describe(entry.Id, entry.Name) is not { } facts
                || !GameSessionJob.BelongsToGame(facts, packageFamilyName))
            {
                continue;
            }

            running = true;
            sawGame = true;
            firstSeen ??= _clock.Elapsed;
            if (_known.Add(entry.Id))
            {
                PackagedLaunchLog.Info(
                    $"+{Seconds(_clock.Elapsed)}s game process {facts.Name} ({facts.Id}), "
                    + $"{(facts.IsAppContainer == true ? "AppContainer" : "full trust")} at "
                    + $"{(facts.Integrity.Length > 0 ? facts.Integrity : "unreadable")} integrity.");
                if (job.Contain(facts.Id, facts.Name))
                {
                    _containedCount++;
                }

                onGameProcess?.Invoke(facts);
            }
        }

        if (!running && seedProcessId > 0 && !sawGame)
        {
            // Nothing carries the package identity yet. Activation returned a seed, so say whether
            // it is still alive: a seed that is already gone with no successor is the shape of a
            // title that refused to start.
            PackagedLaunchLog.Change(
                "seed",
                ProcessInspector.StartedAt(seedProcessId) is null
                    ? $"Activation's process {seedProcessId} is gone and no game process has appeared yet."
                    : $"Waiting for {packageFamilyName}; activation's process {seedProcessId} is running.");
        }

        return running;
    }

    private static string Seconds(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }
}
