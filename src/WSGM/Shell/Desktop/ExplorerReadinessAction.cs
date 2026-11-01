namespace WSGM.Shell;

/// <summary>What the boot-takeover poller should do next.</summary>
public enum ExplorerReadinessAction
{
    /// <summary>Explorer is still initializing (or still settling) — keep polling.</summary>
    Wait,

    /// <summary>
    ///     Shell window and taskbar both exist for the first time — start the
    ///     settle delay that covers Run-key/Startup-folder processing.
    /// </summary>
    BeginSettle,

    /// <summary>Logon prep is done — shut explorer down and take over.</summary>
    Proceed,

    /// <summary>
    ///     A Big Picture window appeared under the opaque boot cover. The occlusion finding in
    ///     <c>docs\boot-and-shell.md</c> says CEF suspends rendering while fully occluded, so it forbids
    ///     staying opaque over the window —
    ///     skip the remaining settle and take over immediately so the splash's normal
    ///     BP-detection fade can lift the occlusion.
    /// </summary>
    ProceedAccelerated,

    /// <summary>
    ///     Explorer never became ready within the hard cap — take over anyway
    ///     rather than covering the screen forever.
    /// </summary>
    ProceedTimeout
}
