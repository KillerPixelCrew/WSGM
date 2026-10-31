using System;

namespace WSGM.Core;

/// <summary>Completes the requested shell exit with a bounded graceful phase and forced fallback.</summary>
/// <remarks>
///     A replacement from Winlogon is handled by the same exit sequence before takeover.
///     Request orderly shell exit, enforce it after the grace, and observe bounded absence;
///     the captured shell process must finish before Game Mode can take ownership.
/// </remarks>
internal static class ExplorerExitPolicy
{
    /// <summary>Task Manager's End Process status; zero selects Explorer restart semantics instead.</summary>
    internal const uint ForcedExitCode = 1;

    /// <summary>Time allowed for the shell to honour its orderly exit request.</summary>
    internal static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(3);

    /// <summary>How long the shell must stay absent before exit counts.</summary>
    /// <remarks>Long enough to see a Winlogon respawn inside the exit step.</remarks>
    internal static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    ///     How long to watch for Winlogon's replacement after a forced or unclean exit before calling the shell gone.
    /// </summary>
    /// <remarks>
    ///     A forced exit is not an orderly shell shutdown, even when termination supplies status zero.
    ///     Device logs put Winlogon's respawned taskbar at about 3 s; racing the tray host against it
    ///     was the 2026-08-08 failure.
    /// </remarks>
    internal static readonly TimeSpan RespawnGrace = TimeSpan.FromSeconds(8);

    /// <summary>Chooses the next exit action from observed shell absence and the retired process state.</summary>
    /// <param name="shellSurfacePresent">Whether any current shell taskbar/desktop surface remains.</param>
    /// <param name="originalExited">Whether the captured original process has exited.</param>
    /// <param name="absentFor">Continuous duration without shell surfaces; reset when any surface reappears.</param>
    /// <param name="uncleanExit">Whether the original process exited abnormally, requiring extra respawn observation.</param>
    /// <param name="requestedFor">Time since orderly exit was requested for this exact shell owner.</param>
    /// <param name="forcedExit">Whether this owner was forcibly terminated, regardless of its supplied exit status.</param>
    /// <returns>Wait, completion, or termination of the captured owner after its grace.</returns>
    internal static ExplorerExitAction Decide(
        bool shellSurfacePresent,
        bool originalExited,
        TimeSpan absentFor,
        bool uncleanExit = false,
        TimeSpan requestedFor = default,
        bool forcedExit = false)
    {
        if (!originalExited && !forcedExit && requestedFor >= GracefulExit)
        {
            return ExplorerExitAction.ForceStop;
        }

        if (!originalExited || shellSurfacePresent || absentFor < StableAbsence)
        {
            return ExplorerExitAction.Wait;
        }

        return (uncleanExit || forcedExit) && absentFor < RespawnGrace
            ? ExplorerExitAction.Wait
            : ExplorerExitAction.Complete;
    }
}
