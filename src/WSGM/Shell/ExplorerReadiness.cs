using System;

namespace WSGM.Shell;

/// <summary>What the boot-takeover poller should do next.</summary>
public enum ExplorerReadinessAction
{
    /// <summary>Explorer is still initializing (or still settling) — keep polling.</summary>
    Wait,

    /// <summary>Shell window and taskbar both exist for the first time — start the
    /// settle delay that covers Run-key/Startup-folder processing.</summary>
    BeginSettle,

    /// <summary>Logon prep is done — shut explorer down and take over.</summary>
    Proceed,

    /// <summary>A Big Picture window appeared under the opaque boot cover. Invariant 7
    /// (CEF suspends rendering while fully occluded) forbids staying opaque over it —
    /// skip the remaining settle and take over immediately so the splash's normal
    /// BP-detection fade can lift the occlusion.</summary>
    ProceedAccelerated,

    /// <summary>Explorer never became ready within the hard cap — take over anyway
    /// rather than covering the screen forever.</summary>
    ProceedTimeout,
}

/// <summary>Pure decision core for "has explorer finished its logon prep?" during a
/// service boot. The signal is deliberately simple and log-diagnosable: explorer's
/// shell window (GetShellWindow) AND its taskbar (Shell_TrayWnd — WSGM's own tray
/// host is not created yet in this flow, so any Shell_TrayWnd is explorer's), then
/// a fixed settle delay. The thin poller in ShellSession supplies the observations.</summary>
public static class ExplorerReadiness
{
    /// <summary>Hard cap on the whole wait; after this the takeover proceeds anyway.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>Decides the next poller action from the current observations.</summary>
    /// <param name="shellWindowPresent">GetShellWindow() returned a window.</param>
    /// <param name="taskbarPresent">A Shell_TrayWnd window exists.</param>
    /// <param name="bigPictureVisible">A Steam Big Picture window exists UNDER THE
    /// BOOT COVER. The caller reports false when no cover is on screen (splash
    /// disabled): there is nothing to accelerate away from then, and skipping the
    /// settle would cut explorer's logon prep short for no reason.</param>
    /// <param name="elapsed">Time since the poll started.</param>
    /// <param name="settleElapsed">Time since the settle began, or null before it did.</param>
    /// <param name="settleDuration">Configured settle delay (ExplorerLogonSettleMs).</param>
    /// <param name="maxWait">Hard cap (pass <see cref="MaxWait"/> outside tests).</param>
    public static ExplorerReadinessAction Decide(
        bool shellWindowPresent, bool taskbarPresent, bool bigPictureVisible,
        TimeSpan elapsed, TimeSpan? settleElapsed, TimeSpan settleDuration, TimeSpan maxWait)
    {
        if (bigPictureVisible)
        {
            return ExplorerReadinessAction.ProceedAccelerated;
        }
        if (elapsed >= maxWait)
        {
            return ExplorerReadinessAction.ProceedTimeout;
        }
        if (settleElapsed is { } settling)
        {
            // Once settling, explorer windows vanishing again (a crash) doesn't
            // reset anything: the takeover shuts explorer down regardless.
            return settling >= settleDuration
                ? ExplorerReadinessAction.Proceed
                : ExplorerReadinessAction.Wait;
        }
        return shellWindowPresent && taskbarPresent
            ? ExplorerReadinessAction.BeginSettle
            : ExplorerReadinessAction.Wait;
    }
}
