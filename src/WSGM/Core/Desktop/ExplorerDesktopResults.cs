using System;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Result of capturing a canonical Explorer and creating its replacement anchor.</summary>
/// <param name="Prepared">Whether a verified anchor is retained before shell exit.</param>
/// <param name="Detail">Diagnostic readiness or refusal reason.</param>
internal readonly record struct ExplorerPreparationResult(
    bool Prepared,
    string Detail);

/// <summary>One sampled observation of Explorer's shell and taskbar surfaces.</summary>
/// <param name="Process">Inspected candidate shell owner, including native query failures.</param>
/// <param name="TaskbarOwnerProcessId">Observed taskbar owner, or zero when absent.</param>
/// <param name="ShellOwnerProcessId">Observed desktop window owner, or zero when absent.</param>
/// <param name="HasShellSurface">Whether any shell surface was observed, even if it was not usable.</param>
/// <param name="Initialized">Whether one owner has both shell surfaces and passed any requested responsiveness probe.</param>
/// <param name="Acceptance">Identity/readiness acceptance result.</param>
/// <param name="Outcome">Quality classification derived from the observations.</param>
internal readonly record struct ExplorerDesktopObservation(
    NativeShellProcessInfo Process,
    uint TaskbarOwnerProcessId,
    uint ShellOwnerProcessId,
    bool HasShellSurface,
    bool Initialized,
    ExplorerShellAcceptance Acceptance,
    ExplorerDesktopOutcome Outcome);

/// <summary>Quality of the restored desktop shell.</summary>
internal enum ExplorerDesktopOutcome
{
    /// <summary>The taskbar owner is canonical, medium-integrity, and jobless.</summary>
    Normal,

    /// <summary>A canonical current-session medium Explorer is usable through recovery only.</summary>
    Degraded,

    /// <summary>No verified usable taskbar was produced.</summary>
    Failed
}

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

/// <summary>Verified result of a desktop restoration attempt.</summary>
internal readonly record struct ExplorerDesktopResult
{
    /// <summary>
    ///     Creates a restoration result while enforcing that the scheduled-task route is
    ///     recovery-only even when its observed process happens to pass the normal shell checks.
    /// </summary>
    /// <param name="outcome">Observed desktop quality; scheduler Normal is downgraded to Degraded.</param>
    /// <param name="route">Launch or adoption mechanism used.</param>
    /// <param name="processId">Observed shell owner PID, or zero when unavailable.</param>
    /// <param name="createdProcessId">PID returned by creation, or zero; it may differ from the shell owner.</param>
    /// <param name="detail">Diagnostic reason for the result.</param>
    /// <param name="launchDispatched">Whether launch occurred or may still complete after an uncertain result.</param>
    /// <param name="shellSurfacePresent">Whether any shell surface remains to be preserved.</param>
    /// <param name="elapsed">Elapsed restoration duration, including serialized admission.</param>
    internal ExplorerDesktopResult(
        ExplorerDesktopOutcome outcome,
        ExplorerDesktopRoute route,
        uint processId,
        uint createdProcessId,
        string detail,
        bool launchDispatched,
        bool shellSurfacePresent,
        TimeSpan elapsed)
    {
        Outcome = route is ExplorerDesktopRoute.ScheduledTaskRecovery
                  && outcome is ExplorerDesktopOutcome.Normal
            ? ExplorerDesktopOutcome.Degraded
            : outcome;
        Route = route;
        ProcessId = processId;
        CreatedProcessId = createdProcessId;
        Detail = detail;
        LaunchDispatched = launchDispatched;
        ShellSurfacePresent = shellSurfacePresent;
        Elapsed = elapsed;
    }

    /// <summary>Gets the verified quality of the restored desktop.</summary>
    internal ExplorerDesktopOutcome Outcome { get; }

    /// <summary>Gets the route that produced the observed desktop.</summary>
    internal ExplorerDesktopRoute Route { get; }

    /// <summary>Gets the process that owns the verified shell surfaces.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets the process identifier returned by the launch operation, if any.</summary>
    internal uint CreatedProcessId { get; }

    /// <summary>Gets the diagnostic result detail.</summary>
    internal string Detail { get; }

    /// <summary>Gets whether an Explorer launch crossed its dispatch boundary.</summary>
    internal bool LaunchDispatched { get; }

    /// <summary>Gets whether any shell surface was observed.</summary>
    internal bool ShellSurfacePresent { get; }

    /// <summary>
    ///     Gets whether a Degraded shell has the same known job-bound quality as the one captured before
    ///     exit, observed in the same stable window. It does not promote the outcome to Normal.
    /// </summary>
    internal bool MatchesCapturedQuality { get; init; }

    /// <summary>Gets the elapsed restoration time.</summary>
    internal TimeSpan Elapsed { get; }
}
