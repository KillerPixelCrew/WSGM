namespace WSGM.Core;

/// <summary>
///     Whether the scheduled recovery request definitely stayed local, definitely reached
///     Task Scheduler, or may have crossed the dispatch boundary.
/// </summary>
internal enum ScheduledTaskLaunchDisposition
{
    /// <summary>No Explorer launch request reached Task Scheduler.</summary>
    NotDispatched,

    /// <summary>Task Scheduler accepted the Explorer launch request.</summary>
    Dispatched,

    /// <summary>The scheduler command began but its dispatch result could not be verified.</summary>
    Unknown
}
