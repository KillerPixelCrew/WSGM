using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.LogonService;

/// <summary>The Windows operations used by one logon service instance.</summary>
internal interface ISessionHost
{
    /// <summary>Whether an unfinished setup transaction blocks a runtime launch.</summary>
    bool HasPendingSetup { get; }

    /// <summary>The calling thread's last Win32 error, to be read immediately after a failed operation.</summary>
    int LastError { get; }

    /// <summary>Opens the session's interactive user token.</summary>
    /// <param name="sessionId">Windows session to query.</param>
    /// <param name="token">Owned token on success; the caller releases it through CloseHandle.</param>
    /// <returns>Whether a token was acquired.</returns>
    bool TryGetUserToken(uint sessionId, out nint token);

    /// <summary>Reads untrusted boot policy from the profile identified by a borrowed token.</summary>
    /// <param name="userToken">Interactive user's token; ownership remains with the caller.</param>
    /// <returns>A usable manifest, or null for unavailable profile, file or policy.</returns>
    BootManifest? ReadManifest(nint userToken);

    /// <summary>Reads TOKEN_ELEVATION_TYPE without transferring token ownership.</summary>
    /// <param name="token">Borrowed token to inspect.</param>
    /// <param name="elevationType">The native enum value on success.</param>
    /// <returns>Whether the query succeeded.</returns>
    bool TryGetElevationType(nint token, out int elevationType);

    /// <summary>Duplicates a linked token as a primary token for the target session.</summary>
    /// <param name="token">Borrowed split-token user token.</param>
    /// <param name="sessionId">Session requested for the duplicate.</param>
    /// <returns>An owned primary token, or zero when unavailable.</returns>
    nint GetLinkedPrimaryToken(nint token, uint sessionId);

    /// <summary>Starts an executable on the interactive desktop with the supplied token's user environment.</summary>
    /// <param name="token">Borrowed primary token that determines launch identity.</param>
    /// <param name="executable">Executable path already selected by the caller.</param>
    /// <param name="arguments">Fixed application arguments, excluding the executable.</param>
    /// <param name="process">Owned process handle on success.</param>
    /// <param name="processId">Created process id on success.</param>
    /// <param name="error">Win32 failure code, or zero on success.</param>
    /// <returns>Whether creation succeeded; no readiness or health handshake is implied.</returns>
    bool TryLaunch(nint token, string executable, string arguments, out nint process, out uint processId,
        out int error);

    /// <summary>Enumerates active sessions with readable logon ages for the startup catch-up sweep.</summary>
    /// <returns>Session ids and nonnegative ages; unreadable sessions are omitted.</returns>
    IEnumerable<(uint SessionId, TimeSpan LogonAge)> ActiveSessions();

    /// <summary>Reads the session's account name for diagnostics.</summary>
    /// <param name="sessionId">Session to inspect.</param>
    /// <returns>The account name or a diagnostic placeholder.</returns>
    string GetSessionUser(uint sessionId);

    /// <summary>Waits on a borrowed process handle.</summary>
    /// <param name="process">Process handle retained by the watchdog.</param>
    /// <param name="milliseconds">Native wait duration; uint.MaxValue waits indefinitely.</param>
    /// <returns>The raw Windows wait result; only WAIT_OBJECT_0 confirms exit.</returns>
    uint WaitForSingleObject(nint process, uint milliseconds);

    /// <summary>Queries a borrowed process handle's exit status.</summary>
    /// <param name="process">Process handle whose wait result is checked separately.</param>
    /// <param name="exitCode">Native exit status on success.</param>
    /// <returns>Whether the query succeeded.</returns>
    bool TryGetExitCode(nint process, out uint exitCode);

    /// <summary>Checks whether Windows still reports the target session active.</summary>
    /// <param name="sessionId">Session associated with the launch.</param>
    /// <returns>False when inactive, absent or unreadable.</returns>
    bool IsSessionActive(uint sessionId);

    /// <summary>Runs the fixed desktop-shell probe as the interactive user with a bounded wait.</summary>
    /// <param name="userToken">Borrowed unlinked token for the target desktop.</param>
    /// <param name="executable">WSGM executable that implements the fixed probe mode.</param>
    /// <returns>Whether the probe confirmed the Explorer desktop shell; failures return false.</returns>
    bool IsDesktopShellInSession(nint userToken, string executable);

    /// <summary>Releases a token or process handle owned by the caller.</summary>
    /// <param name="handle">Owned native handle to close.</param>
    void CloseHandle(nint handle);

    /// <summary>Starts the process-exit observer on a background thread.</summary>
    /// <param name="watch">Observer that owns its retained handles until completion.</param>
    /// <param name="name">Diagnostic thread name.</param>
    void StartWatchdog(Action watch, string name);

    /// <summary>Gives the independent shell anchor time to recover before the fallback probes again.</summary>
    /// <param name="grace">Blocking grace period; this is not an anchor acknowledgement.</param>
    void WaitForAnchor(TimeSpan grace);

    /// <summary>Writes an informational service-log entry.</summary>
    /// <param name="message">Decision or transition text.</param>
    void Info(string message);

    /// <summary>Writes a degraded-operation service-log entry.</summary>
    /// <param name="message">The failure and resulting behavior.</param>
    void Warn(string message);

    /// <summary>Writes a failed-operation service-log entry.</summary>
    /// <param name="message">Failure details without user secrets.</param>
    void Error(string message);
}
