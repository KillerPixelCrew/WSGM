using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>Finds a game's processes in a snapshot of the machine.</summary>
internal interface IGameProcesses
{
    /// <summary>What the session is waiting for, for the log.</summary>
    string Description { get; }

    /// <summary>The game's processes among these.</summary>
    /// <param name="snapshot">Every process running, other than the idle and system processes and this one.</param>
    /// <returns>The game's processes, described.</returns>
    IReadOnlyList<ProcessFacts> Find(IReadOnlyList<ProcessEntry> snapshot);
}

/// <summary>A packaged game's processes: every one that carries its package identity.</summary>
/// <param name="packageFamilyName">The package family.</param>
internal sealed class PackagedGameProcesses(string packageFamilyName) : IGameProcesses
{
    /// <inheritdoc />
    public string Description => packageFamilyName;

    /// <inheritdoc />
    public IReadOnlyList<ProcessFacts> Find(IReadOnlyList<ProcessEntry> snapshot)
    {
        List<ProcessFacts> found = [];
        foreach (var entry in snapshot)
        {
            if (ProcessInspector.PackageFamilyNameOf(entry.Id) is { } family
                && string.Equals(family, packageFamilyName, StringComparison.OrdinalIgnoreCase)
                && ProcessInspector.Describe(entry.Id, entry.Name) is { } facts
                && GameSessionJob.BelongsToGame(facts, packageFamilyName))
            {
                found.Add(facts);
            }
        }

        return found;
    }
}

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
///         Discovery polls, because a game's processes appear over several seconds and Windows offers
///         no notification a non-elevated process can rely on. It polls quickly until the game
///         appears and at the session's own pace after that. Lifetime does not poll the machine:
///         once the game is established the job's own active count answers whether it is still
///         running, which is cheaper and more truthful than re-enumerating the machine.
///     </para>
///     <para>
///         When that count reaches zero the machine is looked at again for as long as the exit grace
///         lasts. A game can restart itself outside the tree that was contained - Epic's online
///         services relaunch a title through the launcher, Battle.net after a patch - and only a
///         fresh look finds the new process before the session is declared over.
///     </para>
///     <para>
///         A process is known by its id together with its start time, never by its id alone: ids are
///         reused, and a new game process that happened to take a finished one's id would otherwise
///         never be contained.
///     </para>
/// </remarks>
internal sealed class GameSessionSupervisor(
    IGameProcesses game,
    GameSessionJob job,
    Action<ProcessFacts>? onGameProcess = null,
    GameSessionTimings? timings = null,
    Func<bool>? startFailed = null)
{
    /// <summary>How often to look for the game before any of it has appeared.</summary>
    private static readonly TimeSpan DiscoveryPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>How often to check a settled session, which only has to notice an exit.</summary>
    private static readonly TimeSpan SettledPoll = TimeSpan.FromSeconds(2);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly HashSet<(int Id, DateTime? StartedAt)> _known = [];

    private readonly GameSessionTimings _timings = timings ?? GameSessionTimings.Packaged;

    /// <summary>How many of the processes seen the job actually accepted.</summary>
    /// <remarks>
    ///     Assignment is allowed to fail. A job that holds only some of the game's processes
    ///     reports zero active as soon as the ones it does hold exit, which looks exactly like the
    ///     game ending; the wrapper would then release Steam while the game is still running. Only
    ///     a job that holds everything seen can be trusted to answer that question.
    /// </remarks>
    private int _containedCount;

    /// <summary>Supervises a packaged game: every process that carries its package identity.</summary>
    /// <param name="packageFamilyName">The package family.</param>
    /// <param name="job">The kill-on-close job the game's processes go into.</param>
    /// <param name="onGameProcess">Hears each game process as it is first seen.</param>
    internal GameSessionSupervisor(
        string packageFamilyName, GameSessionJob job, Action<ProcessFacts>? onGameProcess = null)
        : this(new PackagedGameProcesses(packageFamilyName), job, onGameProcess)
    {
    }

    /// <summary>Whether the session could not do what the user asked of it.</summary>
    internal bool Degraded { get; set; }

    /// <summary>Supervises until the game exits, never appears, or a stop is requested.</summary>
    /// <param name="seedProcessId">The process activation returned, or zero when there is none.</param>
    /// <param name="cancellationToken">Requests a stop, leaving the game running.</param>
    /// <returns>Why the session ended.</returns>
    internal GameSessionOutcome Run(int seedProcessId, CancellationToken cancellationToken)
    {
        var sawGame = false;
        TimeSpan? goneSince = null;
        TimeSpan? firstSeen = null;

        while (true)
        {
            var discoveryOpen = firstSeen is null || _clock.Elapsed - firstSeen < _timings.DiscoveryWindow;
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
                    cancellationToken.IsCancellationRequested,
                    !sawGame && startFailed?.Invoke() == true),
                _timings.Settle,
                _timings.ExitGrace);
            if (outcome is not GameSessionOutcome.Running)
            {
                return outcome;
            }

            cancellationToken.WaitHandle.WaitOne(Poll(firstSeen, goneSince));
        }
    }

    /// <summary>How long to wait before looking again.</summary>
    /// <remarks>
    ///     Quick while nothing has appeared, and while the game is gone and may be restarting: both
    ///     are waiting for a process to show up. At the session's own pace while it is still being
    ///     discovered, and slow once it is settled.
    /// </remarks>
    private TimeSpan Poll(TimeSpan? firstSeen, TimeSpan? goneSince)
    {
        if (firstSeen is null || goneSince is not null)
        {
            return DiscoveryPoll;
        }

        return _clock.Elapsed - firstSeen < _timings.DiscoveryWindow ? _timings.EstablishingPoll : SettledPoll;
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
        //
        // An empty job is not trusted either. Zero active processes may be the game exiting, a game
        // restarting itself outside the contained tree, or - when an assignment was refused, which is
        // legal and normal for a packaged app already in a system job - a running process the kernel
        // is not counting. All three are settled by looking at the machine.
        if (sawGame && !discoveryOpen && job.ActiveProcesses() is > 0)
        {
            return true;
        }

        List<ProcessEntry> candidates = [];
        foreach (var entry in ProcessInspector.Snapshot())
        {
            if (entry.Id > 4 && entry.Id != Environment.ProcessId)
            {
                candidates.Add(entry);
            }
        }

        var running = false;
        foreach (var facts in game.Find(candidates))
        {
            running = true;
            sawGame = true;
            firstSeen ??= _clock.Elapsed;
            if (!_known.Add((facts.Id, facts.StartedAt)))
            {
                continue;
            }

            PackagedLaunchLog.Info(
                $"+{Seconds(_clock.Elapsed)}s game process {facts.Name} ({facts.Id}), "
                + $"{(facts.IsAppContainer == true ? "AppContainer" : "full trust")} at "
                + $"{(facts.Integrity.Length > 0 ? facts.Integrity : "unreadable")} integrity.");
            if (job.Contain(facts))
            {
                _containedCount++;
            }

            onGameProcess?.Invoke(facts);
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
                    : $"Waiting for {game.Description}; activation's process {seedProcessId} is running.");
        }

        return running;
    }

    private static string Seconds(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }
}

/// <summary>How long a supervised session waits at each stage, and how often it looks.</summary>
/// <param name="Settle">How long the game may take to appear at all.</param>
/// <param name="ExitGrace">How long it must be gone before the session is over.</param>
/// <param name="DiscoveryWindow">How long new game processes are looked for after the first one.</param>
/// <param name="EstablishingPoll">How often the machine is looked at during that window.</param>
internal sealed record GameSessionTimings(
    TimeSpan Settle,
    TimeSpan ExitGrace,
    TimeSpan DiscoveryWindow,
    TimeSpan EstablishingPoll)
{
    /// <summary>
    ///     A packaged game, which Windows activates at once. Its window is short and polled quickly:
    ///     a GDK title's launch helper hands over to the game within seconds, and the overlay route
    ///     acts on the game process as soon as it is seen.
    /// </summary>
    internal static GameSessionTimings Packaged { get; } = new(
        GameSessionExitDecision.Settle, GameSessionExitDecision.ExitGrace, TimeSpan.FromSeconds(30),
        TimeSpan.FromMilliseconds(500));

    /// <summary>
    ///     A game another launcher starts: the launcher may update or ask for a sign-in first, and a
    ///     bootstrapper can hand over to the game with a gap between them. Its window is long, so it
    ///     is polled at the settled pace once the game has appeared: nothing acts on a followed
    ///     process except containment, and a late helper is contained two seconds later just the same.
    /// </summary>
    internal static GameSessionTimings Followed { get; } = new(
        TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2));
}
