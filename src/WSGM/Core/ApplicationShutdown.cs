using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The one process-level shutdown policy selected before Avalonia teardown begins.</summary>
internal enum ApplicationShutdownReason
{
    Normal,
    Update,
    SessionEnd,
    Uninstall
}

/// <summary>Bounded outcome of the process-owned graceful shutdown attempt.</summary>
internal enum ApplicationShutdownOutcome
{
    Clean,
    Unverified,
    TimedOut,
    Failed
}

/// <summary>Cross-bootstrap marker used by one-shot exit sources before lifetime shutdown.</summary>
internal static class ApplicationShutdownRequest
{
    private static int _reason;
    private static int _sessionEnding;

    internal static ApplicationShutdownReason Current => (ApplicationShutdownReason)Volatile.Read(ref _reason);
    internal static bool SessionEnding => Volatile.Read(ref _sessionEnding) != 0;

    internal static void Request(ApplicationShutdownReason reason)
    {
        if (reason is ApplicationShutdownReason.SessionEnd)
        {
            Interlocked.Exchange(ref _sessionEnding, 1);
        }

        while (true)
        {
            var current = Volatile.Read(ref _reason);
            var currentReason = (ApplicationShutdownReason)current;
            if (PriorityFor(currentReason) >= PriorityFor(reason)
                || Interlocked.CompareExchange(ref _reason, (int)reason, current) == current)
            {
                return;
            }
        }
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _reason, (int)ApplicationShutdownReason.Normal);
        Interlocked.Exchange(ref _sessionEnding, 0);
    }

    private static int PriorityFor(ApplicationShutdownReason reason)
    {
        return reason switch
        {
            ApplicationShutdownReason.Uninstall => 3,
            ApplicationShutdownReason.Update => 2,
            ApplicationShutdownReason.SessionEnd => 1,
            _ => 0
        };
    }
}

/// <summary>Owns the single process exit attempt independently of Avalonia's shutdown events.</summary>
internal sealed class ApplicationRuntime(
    Func<ApplicationShutdownReason, DateTimeOffset, ValueTask>? sessionShutdown,
    Action<int> forcedExit,
    Action<ApplicationShutdownReason, ApplicationShutdownOutcome> reportHandoff,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _timeout = new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private long _deadlineTicks;
    private volatile Task? _exit;
    private volatile bool _osEnding;
    private volatile bool _startupFailed;

    internal bool ExitRequested => _exit is not null;
    internal bool StartupFailed => _startupFailed;

    internal DateTimeOffset Deadline => new(Interlocked.Read(ref _deadlineTicks), TimeSpan.Zero);

    internal Task RequestExit()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_exit is not null)
            {
                if (!_exit.IsCompleted)
                {
                    TightenDeadline();
                }

                return _exit;
            }

            var reason = ApplicationShutdownRequest.SessionEnding
                ? ApplicationShutdownReason.SessionEnd
                : ApplicationShutdownRequest.Current;
            Interlocked.Exchange(ref _deadlineTicks,
                _utcNow().Add(ApplicationShutdownCoordinator.BudgetFor(reason)).UtcTicks);
            ArmTimeout();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _exit = completion.Task;
        }

        // Publish ownership before invoking delegates, including a synchronous re-entry.
        _ = RunAsync(completion);
        return completion.Task;
    }

    internal Task RequestOsSessionEnd()
    {
        _osEnding = true;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        return RequestExit();
    }

    internal Task StartupFailedExit()
    {
        _startupFailed = true;
        return RequestExit();
    }

    private void TightenDeadline()
    {
        var reason = ApplicationShutdownRequest.SessionEnding
            ? ApplicationShutdownReason.SessionEnd
            : ApplicationShutdownRequest.Current;
        var proposed = _utcNow().Add(ApplicationShutdownCoordinator.BudgetFor(reason));
        if (proposed < Deadline)
        {
            Interlocked.Exchange(ref _deadlineTicks, proposed.UtcTicks);
            ArmTimeout();
        }
    }

    private void ArmTimeout()
    {
        var remaining = Deadline - _utcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _timeout.Cancel();
        }
        else
        {
            _timeout.CancelAfter(remaining);
        }
    }

    private async Task RunAsync(TaskCompletionSource completion)
    {
        var outcome = ApplicationShutdownOutcome.Clean;
        try
        {
            if (sessionShutdown is not null)
            {
                var reason = ApplicationShutdownRequest.SessionEnding
                    ? ApplicationShutdownReason.SessionEnd
                    : ApplicationShutdownRequest.Current;
                outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
                    deadline => sessionShutdown(reason, deadline), reason, null, _utcNow,
                    _ => Task.Delay(Timeout.InfiniteTimeSpan, _timeout.Token), () => Deadline);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Application shutdown failed", ex);
            outcome = ApplicationShutdownOutcome.Failed;
        }
        finally
        {
            try
            {
                reportHandoff(ApplicationShutdownRequest.Current, outcome);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Application shutdown handoff failed", ex);
                outcome = ApplicationShutdownOutcome.Failed;
            }

            try
            {
                if (!_osEnding)
                {
                    forcedExit(StartupFailed ? 1 : ApplicationShutdownCoordinator.ExitCodeFor(outcome));
                }
            }
            finally
            {
                completion.TrySetResult();
                lock (_sync)
                {
                    _timeout.Cancel();
                    _timeout.Dispose();
                }
            }
        }
    }
}

/// <summary>
///     Enforces the single outer process-shutdown deadline. Subsystems retain their protocol phase
///     budgets; this owner prevents any collection of cleanup failures from holding installer or
///     session termination indefinitely.
/// </summary>
internal static class ApplicationShutdownCoordinator
{
    internal static int ExitCodeFor(ApplicationShutdownOutcome outcome)
    {
        return outcome is ApplicationShutdownOutcome.Clean ? 0 : 1;
    }

    internal static TimeSpan BudgetFor(ApplicationShutdownReason reason)
    {
        return reason switch
        {
            ApplicationShutdownReason.Update => TimeSpan.FromSeconds(10),
            ApplicationShutdownReason.SessionEnd => TimeSpan.FromSeconds(5),
            ApplicationShutdownReason.Uninstall => TimeSpan.FromSeconds(20),
            _ => TimeSpan.FromSeconds(15)
        };
    }

    internal static Task<ApplicationShutdownOutcome> ShutdownAsync(
        Func<DateTimeOffset, ValueTask> shutdownAsync,
        ApplicationShutdownReason reason,
        TimeSpan? budgetOverride = null)
    {
        return ShutdownAsync(
            shutdownAsync,
            reason,
            budgetOverride,
            static () => DateTimeOffset.UtcNow,
            static timeout => Task.Delay(timeout));
    }

    /// <summary>
    ///     Test seam for the process deadline clock and timer. Production always supplies
    ///     UTC and <see cref="Task.Delay(TimeSpan)" /> through the overload above.
    /// </summary>
    internal static async Task<ApplicationShutdownOutcome> ShutdownAsync(
        Func<DateTimeOffset, ValueTask> shutdownAsync,
        ApplicationShutdownReason reason,
        TimeSpan? budgetOverride,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, Task> delayAsync,
        Func<DateTimeOffset>? currentDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delayAsync);
        var budget = budgetOverride ?? BudgetFor(reason);
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetOverride));
        }

        var deadline = currentDeadline?.Invoke() ?? utcNow().Add(budget);
        Task cleanup;
        try
        {
            cleanup = shutdownAsync(deadline).AsTask();
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown could not start ({reason})", ex);
            return ApplicationShutdownOutcome.Failed;
        }

        try
        {
            var remaining = (currentDeadline?.Invoke() ?? deadline) - utcNow();
            if (cleanup.IsCompleted)
            {
                // Observe a completed cleanup before classifying the outer deadline. A subsystem
                // TimeoutException is still an unverified cleanup result, while a successful
                // synchronous cleanup that consumed the complete owner budget is an outer timeout.
                await cleanup.ConfigureAwait(false);
                if (remaining > TimeSpan.Zero)
                {
                    return ApplicationShutdownOutcome.Clean;
                }

                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            if (remaining <= TimeSpan.Zero)
            {
                ObserveLateCleanup(cleanup, reason);
                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            var timeout = delayAsync(remaining);
            var completed = await Task.WhenAny(cleanup, timeout).ConfigureAwait(false);
            if (!ReferenceEquals(completed, cleanup))
            {
                ObserveLateCleanup(cleanup, reason);
                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            // Await the cleanup task itself after it wins. In particular, a cleanup task that
            // faults with TimeoutException is an unverified subsystem result, not proof that this
            // process owner's outer timer elapsed.
            await cleanup.ConfigureAwait(false);
            if (currentDeadline is not null && currentDeadline() <= utcNow())
            {
                ReportTimeout(reason, budget);
                return ApplicationShutdownOutcome.TimedOut;
            }

            return ApplicationShutdownOutcome.Clean;
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown was incomplete ({reason})", ex);
            return ApplicationShutdownOutcome.Unverified;
        }
    }

    private static void ReportTimeout(ApplicationShutdownReason reason, TimeSpan budget)
    {
        Log.Warn(
            $"Application shutdown exceeded the {budget.TotalSeconds:0.#} s {reason} budget; "
            + "process exit will release process-owned resources and recovery will reconcile next start.");
    }

    private static void ObserveLateCleanup(Task cleanup, ApplicationShutdownReason reason)
    {
        _ = ObserveAsync(cleanup, reason);
    }

    private static async Task ObserveAsync(Task cleanup, ApplicationShutdownReason reason)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Application shutdown failed after its outer deadline ({reason})", ex);
        }
    }
}
