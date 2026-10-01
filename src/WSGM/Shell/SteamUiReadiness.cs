using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Single policy gate for autonomous CEF work during Steam startup.</summary>
/// <remarks>
///     Two mechanisms consult it. Everything that talks to Steam continuously (the persistent
///     transport behind the patch host, the running-application probe and every static evaluator) is
///     switched at the transport itself by <see cref="TransportShouldBeOpen" />, because a
///     cold-starting Steam opens its CEF port seconds before it has a Big Picture window, and the first
///     connection would otherwise inject the whole native-QAM patch set into that headless session.
///     Operations that run once (the tab boot sync and Steam's startup movie choice) wait through
///     <see cref="RunWhenReadyAsync" /> for the transport to open, as the session's gate reports it
///     through <see cref="Observe" />. Device evidence for both dates is in
///     <c>docs\boot-and-shell.md</c>.
/// </remarks>
internal static class SteamUiReadiness
{
    /// <summary>
    ///     How often the shell re-reads the Big Picture window while it owns the transport
    ///     gate. One second bounds the cold-start delay after the window appears; the boot splash keeps
    ///     its own tighter detection because that one drives a visible fade.
    /// </summary>
    internal static readonly TimeSpan TransportGatePollInterval = TimeSpan.FromSeconds(1);

    private static readonly Lock Sync = new();

    /// <summary>Completed at the next ready edge, then replaced. Guarded by <see cref="Sync" />.</summary>
    private static TaskCompletionSource _nextReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the gate last reported. Guarded by <see cref="Sync" />.</summary>
    private static bool _ready;

    /// <summary>
    ///     Gets whether Steam has progressed beyond process creation to a real
    ///     Big Picture window. A cold-start SharedJSContext can accept evaluations before
    ///     this point; early mutation was the distinguishing state in a device-observed
    ///     startup failure. BOTH conditions are required — a live steam.exe alone is not
    ///     a constructed Big Picture session.
    /// </summary>
    internal static bool IsReady => Steam.IsRunning && Steam.IsBigPictureVisible;

    /// <summary>Decides whether the Steam UI transport may carry any traffic at all.</summary>
    /// <param name="cefMasterEnabled">Whether Steam CEF integration is switched on.</param>
    /// <param name="inGameMode">Whether WSGM owns game mode, where it also owns Steam's start.</param>
    /// <param name="gameModeTransitionPending">
    ///     Whether a transition is about to ask (or has just
    ///     asked) Steam for Big Picture and has not settled yet. The request rebuilds Steam's whole
    ///     front-end, so the hold must begin BEFORE it fires — waiting for the mode flag flips the
    ///     gate seconds after Steam already started bootstrapping against injected state.
    /// </param>
    /// <param name="bigPictureReady">Whether <see cref="IsReady" /> held when the caller sampled it.</param>
    /// <param name="bigPictureExitPending">
    ///     Whether a transition has asked (or is about to ask) Steam to leave Big Picture and has not
    ///     settled yet. Leaving rebuilds Steam's front-end exactly as entering does, so the hold is
    ///     symmetric: every automatic CEF touch stops before the close request fires and resumes only
    ///     once the desktop return has settled.
    /// </param>
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
        bool gameModeTransitionPending,
        bool bigPictureReady,
        bool bigPictureExitPending = false)
    {
        if (!cefMasterEnabled || bigPictureExitPending)
        {
            return false;
        }

        return (!inGameMode && !gameModeTransitionPending) || bigPictureReady;
    }

    /// <summary>
    ///     Records what the session's transport gate just decided. Every change from closed to open is
    ///     a ready edge, which releases the operations waiting in <see cref="RunWhenReadyAsync" />. In
    ///     game mode the gate opens only onto a Big Picture window; on the desktop it opens on the master
    ///     switch, as it does for every other automatic CEF touch there.
    /// </summary>
    /// <param name="ready">Whether the gate holds the transport open.</param>
    internal static void Observe(bool ready)
    {
        TaskCompletionSource reached;
        lock (Sync)
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
    internal static Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
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
    internal static async Task<bool> RunWhenReadyAsync(
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

    private static Task NextReadyAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            return _nextReady.Task.WaitAsync(cancellationToken);
        }
    }
}
