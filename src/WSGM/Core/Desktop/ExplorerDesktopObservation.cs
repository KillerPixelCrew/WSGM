using WSGM.Interop;

namespace WSGM.Core;

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
