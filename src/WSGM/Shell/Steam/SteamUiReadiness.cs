using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Session policy gate shared by autonomous CEF consumers and one-shot startup work.</summary>
/// <remarks>
///     The session is the sole writer. Continuous consumers consult the transport gate; one-shot work
///     waits through <see cref="RunWhenReadyAsync" />. This runtime policy uses session and window state,
///     not Steam-log verification. Before manual CEF debugging, confirm Steam and Big Picture startup
///     in current-run Steam logs; an early debugger connection can hang the entire Steam UI.
/// </remarks>
internal sealed class SteamUiReadiness
{
    /// <summary>
    ///     How often the shell re-reads Steam's processes and Big Picture window while it owns the
    ///     transport gate. One second bounds the cold-start delay after the window appears; the boot
    ///     splash keeps its own tighter detection because that one drives a visible fade.
    /// </summary>
    internal static readonly TimeSpan TransportGatePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How long the Big Picture window of an admitted Steam may be missing before the game-mode gate
    ///     treats it as gone. A window recreated across a display change or a wake is back well inside
    ///     this; a closed Big Picture is not.
    /// </summary>
    internal static readonly TimeSpan BigPictureLossGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long the transport stays closed after the machine resumes, so it reconnects to a settled Steam.</summary>
    internal static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(2);

    /// <summary>How often the gate asks the page whether WSGM's bridge still holds while the transport is open.</summary>
    internal static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(15);

    private readonly Lock _sync = new();

    /// <summary>Completed at the next ready edge, then replaced. Guarded by <see cref="_sync" />.</summary>
    private TaskCompletionSource _nextReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the gate last reported. Guarded by <see cref="_sync" />.</summary>
    private bool _ready;

    /// <summary>Gets whether the gate last held the transport open.</summary>
    /// <remarks>
    ///     The gate's own observed state, not a fresh window sample, so a consumer that checks this and
    ///     then waits for <see cref="NextReadyAsync" /> can never miss an edge the gate already passed.
    /// </remarks>
    internal bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _ready;
            }
        }
    }

    /// <summary>Decides whether the Steam UI transport may carry any traffic at all.</summary>
    /// <param name="cefMasterEnabled">Whether Steam CEF integration is switched on.</param>
    /// <param name="inGameMode">Whether WSGM owns game mode, where it also owns Steam's start.</param>
    /// <param name="holds">The holds in force; see <see cref="SteamUiHold" /> for what each one waits for.</param>
    /// <param name="bigPictureReady">Whether <see cref="BigPictureAdmission" /> admitted the Big Picture window.</param>
    /// <returns>True to open the transport; false to hold every automatic CEF touch.</returns>
    /// <remarks>
    ///     Desktop mode opens on the master switch alone: Steam there is the user's own
    ///     windowed client, not a session WSGM is constructing, and the startup hang has only ever been
    ///     observed while Steam constructs a Big Picture session. The one desktop-mode exception is the
    ///     transition that produced it: driving patches and evaluations into the front-end Steam is
    ///     rebuilding on the way out of Big Picture wedged steamwebhelper for three minutes and, with
    ///     it, the Explorer restart that ran underneath (Claw, 2026-09-26).
    /// </remarks>
    internal static bool TransportShouldBeOpen(
        bool cefMasterEnabled,
        bool inGameMode,
        SteamUiHold holds,
        bool bigPictureReady)
    {
        if (!cefMasterEnabled || (holds & ~SteamUiHold.BigPictureRequest) != SteamUiHold.None)
        {
            return false;
        }

        return (!inGameMode && holds == SteamUiHold.None) || bigPictureReady;
    }

    /// <summary>
    ///     Records what the session's transport gate just decided. Every change from closed to open is
    ///     a ready edge, which releases the operations waiting in <see cref="RunWhenReadyAsync" />. In
    ///     game mode the gate opens only onto a Big Picture window; on the desktop it opens on the master
    ///     switch, as it does for every other automatic CEF touch there.
    /// </summary>
    /// <param name="ready">Whether the gate holds the transport open.</param>
    internal void Observe(bool ready)
    {
        TaskCompletionSource reached;
        lock (_sync)
        {
            if (ready == _ready)
            {
                return;
            }

            _ready = ready;
            if (!ready)
            {
                return;
            }

            reached = _nextReady;
            _nextReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        reached.TrySetResult();
    }

    /// <summary>Completes now when the gate last held the transport open, else at the next ready edge.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes once the transport is open.</returns>
    internal Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _ready ? Task.CompletedTask : _nextReady.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Runs one automatic CEF operation once the transport is open, and again at each later ready edge.
    /// </summary>
    /// <param name="operation">Stable diagnostic name.</param>
    /// <param name="attemptAsync">Returns true when the operation completed, false to try again.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True once the operation completed; false when it was cancelled first.</returns>
    /// <remarks>
    ///     Nothing here polls: the session's transport gate already reads the Big Picture window, and
    ///     its ready edge is what starts an attempt. An attempt that fails waits for the next edge, the
    ///     one a Steam restart or a return to game mode brings, rather than for a timer.
    /// </remarks>
    internal async Task<bool> RunWhenReadyAsync(
        string operation,
        Func<CancellationToken, Task<bool>> attemptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(attemptAsync);
        var ready = WhenReadyAsync(cancellationToken);
        while (true)
        {
            try
            {
                if (!ready.IsCompleted)
                {
                    Log.Info($"{operation}: waiting for the Steam UI transport to open.");
                }

                await ready.ConfigureAwait(false);
                // Taken before the attempt, so an edge that arrives while it runs is not missed.
                ready = NextReadyAsync(cancellationToken);
                if (await attemptAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                Log.Info($"{operation}: not done; trying again when the Steam UI transport next opens.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"{operation} attempt failed: {ex.Message}");
            }
        }
    }

    /// <summary>Completes at the next ready edge.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the gate next opens the transport.</returns>
    internal Task NextReadyAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _nextReady.Task.WaitAsync(cancellationToken);
        }
    }
}
