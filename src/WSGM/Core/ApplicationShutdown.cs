using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The one process-level shutdown policy selected before Avalonia teardown begins.</summary>
internal enum ApplicationShutdownReason
{
    /// <summary>Ordinary resident exit with the normal cleanup budget.</summary>
    Normal,
    /// <summary>Installer replacement; stop within the update handoff budget.</summary>
    Update,
    /// <summary>Windows session termination with the shortest cleanup budget.</summary>
    SessionEnd,
    /// <summary>Installer removal with the longer restoration budget.</summary>
    Uninstall
}

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

/// <summary>Cross-bootstrap marker used by one-shot exit sources before lifetime shutdown.</summary>
internal static class ApplicationShutdownRequest
{
    private static int _reason;
    private static int _sessionEnding;

    /// <summary>Gets the highest-priority reason requested so far: uninstall, update, session end, then normal.</summary>
    internal static ApplicationShutdownReason Current => (ApplicationShutdownReason)Volatile.Read(ref _reason);
    /// <summary>Gets whether any caller reported Windows session termination, independently of reason priority.</summary>
    internal static bool SessionEnding => Volatile.Read(ref _sessionEnding) != 0;

    /// <summary>Records an exit reason without downgrading a stronger existing request.</summary>
    /// <param name="reason">Requested shutdown policy; session end also sets the independent session-ending marker.</param>
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

    /// <summary>Clears process-wide exit markers for isolated policy checks; never call during a live shutdown.</summary>
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
/// <param name="sessionShutdown">Optional session cleanup receiving its policy and absolute UTC deadline.</param>
/// <param name="forcedExit">Terminates the process with the selected exit code unless Windows is already ending the session.</param>
/// <param name="reportHandoff">Publishes the outcome after the bounded cleanup attempt; called once even if late cleanup continues.</param>
/// <param name="utcNow">UTC clock override, or null to use the system clock.</param>
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

    /// <summary>Gets whether the shared exit attempt has been published, including a completed attempt.</summary>
    internal bool ExitRequested => _exit is not null;
    /// <summary>Gets whether startup failure must force a nonzero process exit code.</summary>
    internal bool StartupFailed => _startupFailed;

    /// <summary>Gets the current absolute UTC cleanup deadline; meaningful after exit has been requested.</summary>
    internal DateTimeOffset Deadline => new(Interlocked.Read(ref _deadlineTicks), TimeSpan.Zero);

    /// <summary>Starts the single exit attempt or joins it, tightening its deadline for later urgent requests.</summary>
    /// <returns>The shared completion task; it does not imply cleanup succeeded. Process exit is attempted before completion.</returns>
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

    /// <summary>Marks Windows session termination and joins cleanup without calling the forced-exit delegate.</summary>
    /// <returns>The shared exit completion task.</returns>
    internal Task RequestOsSessionEnd()
    {
        _osEnding = true;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        return RequestExit();
    }

    /// <summary>Marks startup as failed and joins cleanup, forcing exit code 1 on the ordinary termination path.</summary>
    /// <returns>The shared exit completion task.</returns>
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
    /// <summary>Maps a cleanup outcome to the process status exposed to callers.</summary>
    /// <param name="outcome">Final classification of the shutdown attempt.</param>
    /// <returns>Zero only for clean completion; otherwise one.</returns>
    internal static int ExitCodeFor(ApplicationShutdownOutcome outcome)
    {
        return outcome is ApplicationShutdownOutcome.Clean ? 0 : 1;
    }

    /// <summary>Gets the total process cleanup budget for an exit reason.</summary>
    /// <param name="reason">Shutdown policy selected by the process owner.</param>
    /// <returns>10 seconds for update, 5 for session end, 20 for uninstall, or 15 for normal exit.</returns>
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

    /// <summary>Runs cleanup within the reason-specific outer deadline using the system UTC clock.</summary>
    /// <param name="shutdownAsync">Starts cleanup once and receives its absolute UTC deadline.</param>
    /// <param name="reason">Determines the default budget and diagnostic context.</param>
    /// <param name="budgetOverride">Positive replacement budget, or null for the reason-specific default.</param>
    /// <returns>The completion classification; timing out does not cancel the cleanup task.</returns>
    /// <exception cref="ArgumentNullException">The cleanup delegate is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selected budget is not positive.</exception>
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
    ///     Runs cleanup against caller-supplied timing and an optional deadline that can tighten
    ///     while cleanup is running. Late work is observed but is not canceled by this coordinator.
    /// </summary>
    /// <param name="shutdownAsync">Starts cleanup once and receives its absolute UTC deadline.</param>
    /// <param name="reason">Determines the default budget and diagnostic context.</param>
    /// <param name="budgetOverride">Positive replacement budget, or null for the reason-specific default.</param>
    /// <param name="utcNow">Clock used to compare the absolute deadline.</param>
    /// <param name="delayAsync">Creates the outer deadline task; completion or cancellation means the budget elapsed.</param>
    /// <param name="currentDeadline">Optional source of a deadline that another exit request may shorten.</param>
    /// <returns>Clean, unverified, failed-to-start, or timed-out completion; late cleanup faults remain observed.</returns>
    /// <exception cref="ArgumentNullException">A required delegate is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selected budget is not positive.</exception>
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
