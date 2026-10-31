using System;
using System.Threading.Tasks;

namespace WSGM.Core;

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
