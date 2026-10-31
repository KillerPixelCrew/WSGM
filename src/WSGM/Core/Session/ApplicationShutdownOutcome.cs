namespace WSGM.Core;

/// <summary>Bounded outcome of the process-owned graceful shutdown attempt.</summary>
internal enum ApplicationShutdownOutcome
{
    /// <summary>Cleanup completed before the outer deadline.</summary>
    Clean,

    /// <summary>Cleanup began but failed to prove completion.</summary>
    Unverified,

    /// <summary>The outer deadline elapsed; cleanup may still be running.</summary>
    TimedOut,

    /// <summary>Cleanup could not start, or the process owner failed.</summary>
    Failed
}
