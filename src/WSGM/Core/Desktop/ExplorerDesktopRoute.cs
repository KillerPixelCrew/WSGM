namespace WSGM.Core;

/// <summary>Route used to obtain the observed desktop.</summary>
internal enum ExplorerDesktopRoute
{
    /// <summary>An already-running valid shell was adopted.</summary>
    ExistingShell,

    /// <summary>The captured fixed-purpose anchor started Explorer.</summary>
    ShellAnchor,

    /// <summary>The scheduled-task path restored a usable but recovery-only shell.</summary>
    ScheduledTaskRecovery
}
