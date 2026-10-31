using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Stateless sampler of the Explorer desktop. The session host and the shell anchor's
///     out-of-process mode both observe through it.
/// </summary>
internal static class ExplorerDesktop
{
    /// <summary>
    ///     Observes both shell surfaces and requires GetShellWindow and Shell_TrayWnd to have
    ///     the same owner. Restored desktops also require responsive windows; launch-parent capture
    ///     only needs the process identity and token.
    /// </summary>
    /// <param name="expectedSessionId">Session whose canonical Explorer must own the shell surfaces.</param>
    /// <param name="requireResponsive">Whether to probe shell responsiveness in addition to identity/ownership.</param>
    /// <returns>One sampled observation; windows and processes may change immediately afterward.</returns>
    internal static ExplorerDesktopObservation Observe(
        int expectedSessionId, bool requireResponsive = true)
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var taskbarPresent = taskbar != 0 && NativeMethods.IsWindow(taskbar);
        uint taskbarOwner = 0;
        if (taskbarPresent)
        {
            NativeMethods.GetWindowThreadProcessId(taskbar, out taskbarOwner);
        }

        var shellWindow = NativeMethods.GetShellWindow();
        var shellPresent = shellWindow != 0 && NativeMethods.IsWindow(shellWindow);
        uint shellOwner = 0;
        if (shellPresent)
        {
            NativeMethods.GetWindowThreadProcessId(shellWindow, out shellOwner);
        }

        var processId = taskbarOwner != 0 ? taskbarOwner : shellOwner;
        var process = processId == 0
            ? NativeShellProcessInfo.Unavailable(0, 0)
            : NativeShellProcess.Inspect(processId);
        var ownsSurfaces = ExplorerShellPolicy.OwnsShellSurfaces(
            taskbarPresent,
            shellPresent,
            taskbarOwner,
            shellOwner);
        var responsive = !requireResponsive
                         || (ownsSurfaces && IsResponsive(taskbar) && IsResponsive(shellWindow));
        var ready = ownsSurfaces && responsive;
        var acceptance = ExplorerShellPolicy.Evaluate(
            process,
            ExplorerControl.ExplorerPath,
            expectedSessionId,
            ready,
            true);
        // Only the liveness probe failed: the canonical Explorer owns both surfaces and passed every
        // identity check. Name that separately so the desktop is reported as degraded rather than
        // absent when a third-party window blocks Explorer's UI thread (Steam Big Picture, 2026-09-26).
        if (!acceptance.Accepted
            && acceptance.Rejection is ExplorerShellRejection.NotReady
            && ownsSurfaces)
        {
            acceptance = new ExplorerShellAcceptance(false, ExplorerShellRejection.ShellUnresponsive);
        }

        var outcome = ExplorerShellPolicy.ClassifyDesktop(
            acceptance,
            ExplorerDesktopRoute.ExistingShell);
        return new ExplorerDesktopObservation(
            process,
            taskbarOwner,
            shellOwner,
            taskbarPresent || shellPresent,
            ready,
            acceptance,
            outcome);
    }

    private static bool IsResponsive(nint window)
    {
        return NativeMethods.SendMessageTimeoutW(window, 0, 0, 0, NativeMethods.SmtoAbortIfHung,
            500, out _) != 0;
    }
}
