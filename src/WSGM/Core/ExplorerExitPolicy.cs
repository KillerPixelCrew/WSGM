using System;

namespace WSGM.Core;

/// <summary>Next nonterminating action while the captured Explorer retires its desktop shell.</summary>
internal enum ExplorerExitAction
{
    /// <summary>Continue observing ownership and absence without dispatching another request.</summary>
    Wait,
    /// <summary>Ask windows owned by the retired process to close; never terminate the process.</summary>
    RequestClose,
    /// <summary>The shell has remained absent long enough to proceed, even if a retired process lingers.</summary>
    Complete
}

/// <summary>Explorer is only ever asked to leave. Nothing in the entry terminates it.</summary>
/// <remarks>
///     Forced termination can trigger Winlogon's AutoRestartShell and race desktop takeover.
///     Request orderly exit, then close only the retired owner's windows and observe bounded absence;
///     a lingering process without shell surfaces may remain alive beside Game Mode.
/// </remarks>
internal static class ExplorerExitPolicy
{
    /// <summary>How long the shell must stay absent before exit counts.</summary>
    /// <remarks>Long enough to see a Winlogon respawn inside the exit step.</remarks>
    internal static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long the retired process may keep running before it is asked to close its windows.</summary>
    internal static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     How long to watch for Winlogon's replacement after an unclean exit before calling the shell gone.
    /// </summary>
    /// <remarks>
    ///     AutoRestartShell relaunches a shell that "stopped unexpectedly", never one that exited cleanly,
    ///     and a non-zero exit code is that unexpected stop. Device logs put the respawned taskbar at
    ///     about 3 s; racing the tray host against it was the 2026-08-08 failure.
    /// </remarks>
    internal static readonly TimeSpan RespawnGrace = TimeSpan.FromSeconds(8);

    /// <summary>How long a retired process that owns no shell may linger before entry proceeds without it.</summary>
    /// <remarks>
    ///     It holds no taskbar or desktop window, so Game Mode can run beside it; the tray host checks the
    ///     desktop shell, not the process.
    /// </remarks>
    internal static readonly TimeSpan LingerLimit = TimeSpan.FromSeconds(10);

    /// <summary>Chooses the next exit action from observed shell absence and the retired process state.</summary>
    /// <param name="shellSurfacePresent">Whether any current shell taskbar/desktop surface remains.</param>
    /// <param name="originalExited">Whether the captured original process has exited.</param>
    /// <param name="absentFor">Continuous duration without shell surfaces; reset when any surface reappears.</param>
    /// <param name="closeRequested">Whether WM_CLOSE has already been dispatched to the retired owner.</param>
    /// <param name="uncleanExit">Whether the original process exited abnormally, requiring extra respawn observation.</param>
    /// <returns>Wait, one close request, or completion; never a termination decision.</returns>
    internal static ExplorerExitAction Decide(
        bool shellSurfacePresent,
        bool originalExited,
        TimeSpan absentFor,
        bool closeRequested,
        bool uncleanExit = false)
    {
        if (shellSurfacePresent || absentFor < StableAbsence)
        {
            return ExplorerExitAction.Wait;
        }

        if (originalExited)
        {
            return uncleanExit && absentFor < RespawnGrace ? ExplorerExitAction.Wait : ExplorerExitAction.Complete;
        }

        if (absentFor >= LingerLimit)
        {
            return ExplorerExitAction.Complete;
        }

        return !closeRequested && absentFor >= CloseAfter
            ? ExplorerExitAction.RequestClose
            : ExplorerExitAction.Wait;
    }
}
