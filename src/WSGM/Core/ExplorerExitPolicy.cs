using System;

namespace WSGM.Core;

internal enum ExplorerExitAction
{
    Wait,
    RequestClose,
    Complete
}

/// <summary>Explorer is only ever asked to leave. Nothing in the entry terminates it.</summary>
/// <remarks>
///     Terminating the shell process is what Winlogon's AutoRestartShell answers with a respawned
///     Explorer (2026-08-08, b1c3958a). That lesson was lost when 41f8251d released a lingering process
///     after 2 s, and an Xbox Ally X, whose orderly exit takes longer, then fought a respawn on every
///     entry (2026-09-25). A retired process is asked to close its windows, as Task Manager's End task
///     asks, and otherwise left to finish on its own.
/// </remarks>
internal static class ExplorerExitPolicy
{
    /// <summary>How long the shell must stay absent before exit counts.</summary>
    /// <remarks>Long enough to see a Winlogon respawn inside the exit step.</remarks>
    internal static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long the retired process may keep running before it is asked to close its windows.</summary>
    internal static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long a retired process that owns no shell may linger before entry proceeds without it.</summary>
    /// <remarks>
    ///     It holds no taskbar or desktop window, so Game Mode can run beside it; the tray host checks the
    ///     desktop shell, not the process.
    /// </remarks>
    internal static readonly TimeSpan LingerLimit = TimeSpan.FromSeconds(10);

    internal static ExplorerExitAction Decide(
        bool shellSurfacePresent,
        bool originalExited,
        TimeSpan absentFor,
        bool closeRequested)
    {
        if (shellSurfacePresent || absentFor < StableAbsence)
        {
            return ExplorerExitAction.Wait;
        }

        if (originalExited || absentFor >= LingerLimit)
        {
            return ExplorerExitAction.Complete;
        }

        return !closeRequested && absentFor >= CloseAfter
            ? ExplorerExitAction.RequestClose
            : ExplorerExitAction.Wait;
    }
}
