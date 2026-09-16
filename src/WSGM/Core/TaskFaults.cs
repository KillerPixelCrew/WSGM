using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Observes the faults of work nobody awaits.</summary>
internal static class TaskFaults
{
    /// <summary>
    ///     Marks a fault on <paramref name="task" /> as observed, so an abandoned cancellation or
    ///     timed-out worker never surfaces later as an unobserved task exception.
    /// </summary>
    /// <param name="task">The detached task.</param>
    internal static void ObserveFaults(this Task task)
    {
        _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
