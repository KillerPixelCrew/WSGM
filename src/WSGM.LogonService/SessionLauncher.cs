using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WSGM.LogonService;

/// <summary>
///     Per-session launch state and the CreateProcessAsUser plumbing. One WSGM
///     launch per logon; a watchdog thread restores explorer if WSGM dies dirty in an
///     explorer-less session. All token work is legal here because the service runs as
///     SYSTEM (SeTcbPrivilege) — the linked-token route that fails with error 1346
///     from user land works fine from this side.
/// </summary>
/// <param name="host">Windows operations; handles acquired through it remain owned by each admitted session.</param>
internal sealed class SessionLauncher(ISessionHost host)
{
    /// <summary>
    ///     WAIT_OBJECT_0 — anything else out of the watchdog's wait means the
    ///     process state could not be observed.
    /// </summary>
    private const uint WaitObject0 = 0;

    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    /// <summary>Startup catch-up window: sessions logged on longer ago are stale.</summary>
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan AnchorRecoveryGrace = TimeSpan.FromSeconds(5);
    private readonly Lock Gate = new();
    private readonly HashSet<uint> InFlight = [];
    private readonly Dictionary<uint, SessionState> Sessions = new();

    private readonly ISessionHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private bool _stopping;

    /// <summary>
    ///     Handles a logon (live SESSIONCHANGE event: <paramref name="logonAge" />
    ///     null; startup catch-up: the measured age). Runs on a worker thread.
    /// </summary>
    /// <param name="sessionId">Interactive session receiving the event or catch-up observation.</param>
    /// <param name="logonAge">Measured age for catch-up, or null for a live logon event.</param>
    internal void OnSessionLogon(uint sessionId, TimeSpan? logonAge)
    {
        bool alreadyLaunched;
        lock (Gate)
        {
            if (_stopping)
            {
                return;
            }

            // Live logon and catch-up can race; admission and duplicate detection share this lock.
            alreadyLaunched = Sessions.ContainsKey(sessionId) || !InFlight.Add(sessionId);
        }

        try
        {
            HandleLogon(sessionId, logonAge, alreadyLaunched);
        }
        finally
        {
            if (!alreadyLaunched)
            {
                lock (Gate)
                {
                    InFlight.Remove(sessionId);
                }
            }
        }
    }

    /// <summary>Closes launch/recovery admission; existing watchdogs retain handles until their waits finish.</summary>
    internal void Stop()
    {
        lock (Gate)
        {
            _stopping = true;
        }
    }

    private void HandleLogon(uint sessionId, TimeSpan? logonAge, bool alreadyLaunched)
    {
        if (_host.HasPendingSetup)
        {
            _host.Warn(
                "Sign-in startup skipped: WSGM setup has an incomplete file transaction. Run setup to repair it.");
            return;
        }

        if (!_host.TryGetUserToken(sessionId, out var userToken))
        {
            _host.Warn($"Session {sessionId}: WTSQueryUserToken failed (error {_host.LastError}).");
            return;
        }

        var launched = false;
        try
        {
            var manifest = _host.ReadManifest(userToken);
            if (manifest is not null && !File.Exists(manifest.ExePath))
            {
                _host.Warn(
                    $"Session {sessionId}: manifest exe missing ({manifest.ExePath}) — treating as no manifest.");
                manifest = null;
            }

            var action = LogonDecision.Decide(manifest, alreadyLaunched, logonAge > CatchUpWindow);
            _host.Info($"Session {sessionId} ({_host.GetSessionUser(sessionId)}): manifest " +
                       (manifest is null
                           ? "absent/unusable"
                           : $"enabled={manifest.GameModeBoot} elevate={manifest.Elevate} exe={manifest.ExePath}") +
                       $" -> {action}.");
            if (action is not (LogonAction.Launch or LogonAction.LaunchElevated))
            {
                return;
            }

            var launchToken = userToken;
            var tokenKind = "user token";
            if (action == LogonAction.LaunchElevated)
            {
                launchToken = TryGetElevatedToken(userToken, sessionId, out tokenKind);
            }

            try
            {
                SessionState state;
                lock (Gate)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    var arguments = LogonDecision.ArgumentsFor(manifest!);
                    if (!_host.TryLaunch(launchToken, manifest!.ExePath, arguments, out var hProcess, out var pid,
                            out var error))
                    {
                        // Explorer is the registered shell, so a failed launch leaves the ordinary desktop.
                        _host.Error($"Session {sessionId}: CreateProcessAsUser failed (error {error}).");
                        return;
                    }

                    _host.Info($"Launching WSGM {arguments} into session {sessionId} ({tokenKind}) — pid {pid}.");

                    state = new SessionState
                    {
                        UserToken = userToken, ProcessHandle = hProcess, ProcessId = pid, Executable = manifest.ExePath
                    };
                    Sessions[sessionId] = state;

                    launched = true;
                }


                _host.StartWatchdog(() => Watch(sessionId, state), $"wsgm-watchdog-{sessionId}");
            }
            finally
            {
                if (launchToken != userToken)
                {
                    _host.CloseHandle(launchToken);
                }
            }
        }
        finally
        {
            // The unlinked user token stays alive inside the session state (the
            // watchdog's explorer fallback needs it); close it only on skip paths.
            if (!launched)
            {
                _host.CloseHandle(userToken);
            }
        }
    }

    /// <summary>
    ///     Clears one session's state on logoff. The handles belong to the
    ///     watchdog thread, which may still be waiting on them — it closes them when
    ///     the launched process exits.
    /// </summary>
    /// <param name="sessionId">Windows session whose launch and health-watch state should be retired.</param>
    internal void OnSessionLogoff(uint sessionId)
    {
        lock (Gate)
        {
            if (Sessions.Remove(sessionId))
            {
                _host.Info($"Session {sessionId} logoff — clearing state.");
            }
        }
    }

    /// <summary>
    ///     Startup catch-up: an auto-start service can lose the race against an
    ///     autologon — launch into any session that logged on within the window and has
    ///     no WSGM yet. Sessions the service already knows are skipped by the decision.
    /// </summary>
    internal void CatchUpExistingSessions()
    {
        foreach (var (sessionId, logonAge) in _host.ActiveSessions())
        {
            _host.Info($"Startup catch-up: session {sessionId} logged on {(int)logonAge.TotalSeconds} s ago.");
            OnSessionLogon(sessionId, logonAge);
        }
    }

    private void Watch(uint sessionId, SessionState state)
    {
        try
        {
            var waitResult = _host.WaitForSingleObject(state.ProcessHandle, uint.MaxValue);
            if (waitResult != WaitObject0)
            {
                _host.Warn($"Session {sessionId}: waiting on WSGM (pid {state.ProcessId}) returned " +
                           $"0x{waitResult:X8} (error {_host.LastError}).");
            }

            var exitKnown = _host.TryGetExitCode(state.ProcessHandle, out var exitCode);
            if (!exitKnown)
            {
                _host.Warn($"Session {sessionId}: GetExitCodeProcess for pid {state.ProcessId} failed " +
                           $"(error {_host.LastError}).");
            }

            // An unknown exit status must fail TOWARDS the fallback: this is the
            // path that keeps a user from sitting in front of a desktop-less
            // session, so "we could not tell" counts as a dirty exit.
            var dirtyExit = !exitKnown || waitResult != WaitObject0 || exitCode != 0;
            var sessionActive = _host.IsSessionActive(sessionId);
            var explorerRunning = _host.IsDesktopShellInSession(state.UserToken, state.Executable);
            _host.Info($"WSGM (pid {state.ProcessId}, session {sessionId}) exited code " +
                       $"{(exitKnown ? exitCode.ToString() : "unknown")} — " +
                       $"session active={sessionActive}, explorer running={explorerRunning}.");
            if (sessionActive && dirtyExit && !explorerRunning)
            {
                // A normal shell session owns a medium/jobless anchor that observes the same WSGM
                // process handle and restores Explorer after owner loss. Give that narrow path one
                // bounded window to publish its shell before the SYSTEM watchdog uses its robust
                // token fallback; otherwise both creators race and the fallback can win with the
                // job-bound process semantics the anchor exists to avoid. The grace is one wait and
                // one look, because every look starts a desktop probe process; looking sooner would
                // change nothing the user sees, since a shell the anchor restored needs no fallback.
                _host.WaitForAnchor(AnchorRecoveryGrace);
                sessionActive = _host.IsSessionActive(sessionId);
                explorerRunning = _host.IsDesktopShellInSession(state.UserToken, state.Executable);
                if (!sessionActive)
                {
                    _host.Info(
                        $"Session {sessionId}: explorer fallback skipped because the session ended during anchor grace.");
                }
                else if (explorerRunning)
                {
                    _host.Info(
                        $"Session {sessionId}: explorer appeared during anchor grace; SYSTEM fallback skipped.");
                }
            }

            if (!sessionActive || !dirtyExit || explorerRunning)
            {
                return;
            }

            // One explorer fallback per logon, always with the UNLINKED user
            // token — explorer must run unelevated (elevated explorer breaks
            // UWP / the touch keyboard). WSGM itself is never relaunched here;
            // its crash-loop breaker owns that story across sign-ins.
            _host.Warn($"Session {sessionId}: WSGM died dirty without a desktop — starting explorer fallback.");
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!_host.TryLaunch(state.UserToken, explorer, "", out var hExplorer, out _, out var error))
            {
                _host.Error($"Session {sessionId}: explorer fallback failed (error {error}).");
            }
            else
            {
                _host.CloseHandle(hExplorer);
            }
        }
        catch (Exception ex)
        {
            _host.Error($"Watchdog for session {sessionId} failed: {ex.Message}");
        }
        finally
        {
            // The watchdog OWNS both handles for its whole lifetime — logoff only
            // drops the dictionary entry. Closing them from there would pull them
            // out from under the wait/query above, and a recycled handle value
            // could then be handed to CreateProcessAsUser as a foreign token.
            var processHandle = state.ProcessHandle;
            var userToken = state.UserToken;
            state.ProcessHandle = 0;
            state.UserToken = 0;
            if (processHandle != 0)
            {
                _host.CloseHandle(processHandle);
            }

            if (userToken != 0)
            {
                _host.CloseHandle(userToken);
            }
        }
    }

    private nint TryGetElevatedToken(nint userToken, uint sessionId, out string tokenKind)
    {
        tokenKind = "user token";
        if (!_host.TryGetElevationType(userToken, out var elevationType))
        {
            _host.Warn(
                $"Session {sessionId}: TokenElevationType query failed (error {_host.LastError}) — launching unelevated.");
            return userToken;
        }

        if (elevationType == TokenElevationTypeFull)
        {
            tokenKind = "already-elevated user token";
            return userToken;
        }

        if (elevationType != TokenElevationTypeLimited)
        {
            _host.Info(
                $"Session {sessionId}: user is elevation-incapable (type {elevationType}) — launching unelevated.");
            return userToken;
        }

        var primary = _host.GetLinkedPrimaryToken(userToken, sessionId);
        if (primary == 0)
        {
            return userToken;
        }

        tokenKind = "linked token";
        return primary;
    }

    private sealed class SessionState
    {
        public string Executable = string.Empty;
        public nint ProcessHandle;
        public uint ProcessId;
        public nint UserToken;
    }
}
