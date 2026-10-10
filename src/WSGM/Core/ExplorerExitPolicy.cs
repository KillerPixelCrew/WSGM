using System;

namespace WSGM.Core;

/// <summary>Next nonterminating action while the captured Explorer retires its desktop shell.</summary>
internal enum ExplorerExitAction
{
    /// <summary>Continue observing ownership and absence without dispatching another request.</summary>
    Wait,

    /// <summary>The shell has remained absent long enough to proceed, even if a retired process lingers.</summary>
    Complete
}

/// <summary>Explorer is only ever asked to leave. Nothing in the entry terminates it.</summary>
/// <remarks>
///     Forced termination can trigger Winlogon's AutoRestartShell and race desktop takeover.
///     Request orderly shell exit and observe bounded absence;
///     a lingering process without shell surfaces may remain alive beside Game Mode.
/// </remarks>
internal static class ExplorerExitPolicy
{
    /// <summary>How long the shell must stay absent before exit counts.</summary>
    /// <remarks>Long enough to see a Winlogon respawn inside the exit step.</remarks>
    internal static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    ///     How long to watch for Winlogon's replacement after an unclean exit before calling the shell gone.
    /// </summary>
    /// <remarks>
    ///     AutoRestartShell relaunches a shell that "stopped unexpectedly", never one that exited cleanly,
    ///     and a non-zero exit code is that unexpected stop. Device logs put the respawned taskbar at
    ///     about 3 s; racing the tray host against it was the 2026-08-08 failure.
    /// </remarks>
    internal static readonly TimeSpan RespawnGrace = TimeSpan.FromSeconds(8);

    /// <summary>Chooses the next exit action from observed shell absence and the retired process state.</summary>
    /// <param name="shellSurfacePresent">Whether any current shell taskbar/desktop surface remains.</param>
    /// <param name="originalExited">Whether the captured original process has exited.</param>
    /// <param name="absentFor">Continuous duration without shell surfaces; reset when any surface reappears.</param>
    /// <param name="uncleanExit">Whether the original process exited abnormally, requiring extra respawn observation.</param>
    /// <returns>Wait or completion; never a request to close unrelated windows or terminate the process.</returns>
    internal static ExplorerExitAction Decide(
        bool shellSurfacePresent,
        bool originalExited,
        TimeSpan absentFor,
        bool uncleanExit = false)
    {
        if (shellSurfacePresent || absentFor < StableAbsence)
        {
            return ExplorerExitAction.Wait;
        }

        return originalExited && uncleanExit && absentFor < RespawnGrace
            ? ExplorerExitAction.Wait
            : ExplorerExitAction.Complete;
    }
}
