using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Observes the faults of work nobody awaits.</summary>
internal static class TaskFaults
{
    /// <summary>
    ///     Observes a detached task's eventual fault without waiting, logging, or canceling its work.
    ///     Use after a caller abandons a worker; this does not make the worker safe to dispose.
    /// </summary>
    /// <param name="task">The detached task.</param>
    internal static void ObserveFaults(this Task task)
    {
        _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
