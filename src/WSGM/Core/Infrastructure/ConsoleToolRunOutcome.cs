namespace WSGM.Core;

/// <summary>
///     Bounded console-process outcome preserving whether process start crossed an uncertain
///     side-effect boundary.
/// </summary>
internal enum ConsoleToolRunOutcome
{
    /// <summary>The process was never started.</summary>
    NotStarted,

    /// <summary>The process exited successfully before the deadline.</summary>
    Succeeded,

    /// <summary>The process exited with a known failure before the deadline.</summary>
    Failed,

    /// <summary>The process started but its result could not be verified.</summary>
    Unknown
}
