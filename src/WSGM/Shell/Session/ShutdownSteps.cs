using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs the steps of one ordered session cleanup: every step is attempted whatever failed before
///     it, every wait is bounded by the same deadline, and what failed is collected for one report.
/// </summary>
/// <param name="deadlineToken">Cancelled when the shutdown's deadline is reached or tightened.</param>
/// <param name="readDeadline">Reads the shutdown's deadline now; a later urgent request can tighten it.</param>
internal sealed class ShutdownSteps(CancellationToken deadlineToken, Func<DateTimeOffset> readDeadline)
{
    // Failures are collected and reported once at the end, so the outer coordinator records the
    // shutdown as unverified without any step having been skipped.
    private readonly ConcurrentQueue<Exception> _failures = new();

    /// <summary>The shutdown's deadline token, for owners that bound their own waits by it.</summary>
    internal CancellationToken Token => deadlineToken;

    /// <summary>The shutdown's deadline as it stands now, for owners that take a deadline rather than a token.</summary>
    internal DateTimeOffset Deadline => readDeadline();

    /// <summary>The failures collected so far, in the order they happened.</summary>
    internal IReadOnlyCollection<Exception> Failures => _failures;

    /// <summary>Runs one shutdown step, keeping its failure so every later step still runs.</summary>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    internal void Step(string message, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordFailure(message, ex);
        }
    }

    /// <summary>Clears an owner's field and disposes the owner as one shutdown step.</summary>
    /// <param name="owner">What the owner is, for the log.</param>
    /// <param name="field">The field holding the owner; null afterwards.</param>
    internal void DisposeOwner<T>(string owner, ref T? field)
        where T : class, IDisposable
    {
        var value = field;
        field = null;
        Step($"Disposing the {owner} failed", () => value?.Dispose());
    }

    /// <summary>Runs one asynchronous shutdown step, keeping its failure so every later step still runs.</summary>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    /// <returns>Completion of the step, which never faults.</returns>
    internal async Task StepAsync(string message, Func<Task> step)
    {
        try
        {
            await JoinWithinDeadlineAsync(step(), deadlineToken, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordFailure(message, ex);
        }
    }

    /// <summary>Runs one synchronous shutdown step on the UI thread, where the owner was built.</summary>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    /// <returns>Completion of the step, which never faults.</returns>
    internal Task UiStepAsync(string message, Action step)
    {
        return StepAsync(message, async () => await Dispatcher.UIThread.InvokeAsync(step));
    }

    /// <summary>Joins work within the deadline as one step; work that ended by cancellation is finished.</summary>
    /// <param name="name">What the work is, for the log.</param>
    /// <param name="work">The work to join.</param>
    /// <returns>Completion of the join, which never faults.</returns>
    internal Task JoinAsync(string name, Task work)
    {
        return StepAsync($"{name} failed or did not finish during application shutdown", async () =>
        {
            try
            {
                await JoinWithinDeadlineAsync(work, deadlineToken, name).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown cancelled it, and cancelled work is finished work.
            }
        });
    }

    /// <summary>Keeps a failed shutdown step for the final report and logs it now.</summary>
    /// <param name="message">What failed, for the log.</param>
    /// <param name="ex">The failure.</param>
    internal void RecordFailure(string message, Exception ex)
    {
        _failures.Enqueue(ex);
        Log.Error(message, ex);
    }

    /// <summary>Keeps an unverified outcome for the final report without logging it again.</summary>
    /// <param name="ex">The outcome, as an exception.</param>
    internal void Record(Exception ex)
    {
        _failures.Enqueue(ex);
    }

    /// <summary>Throws the collected failures as one report, or returns when every step was verified.</summary>
    internal void ThrowIfUnverified()
    {
        if (ShutdownFailure(_failures.ToArray()) is { } unverified)
        {
            throw unverified;
        }
    }

    /// <summary>Awaits work started before shutdown, giving up at the deadline rather than waiting on.</summary>
    /// <param name="work">The work to join.</param>
    /// <param name="deadlineCancellation">Cancelled when the shutdown's deadline is reached or tightened.</param>
    /// <param name="name">What the work is, for the timeout message.</param>
    /// <returns>Completion of the underlying work, propagating its failure or cancellation.</returns>
    /// <exception cref="TimeoutException">The deadline expired before the work finished; the work itself is not stopped.</exception>
    internal static async Task JoinWithinDeadlineAsync(Task work, CancellationToken deadlineCancellation, string name)
    {
        if (work.IsCompleted)
        {
            await work.ConfigureAwait(false);
            return;
        }

        try
        {
            await work.WaitAsync(deadlineCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            work.ObserveFaults();
            throw new TimeoutException($"{name} remains active at the shutdown deadline.");
        }
    }

    /// <summary>Reports collected cleanup failures once, or null when every step was verified.</summary>
    /// <remarks>
    ///     The single-failure case keeps that exception as the inner one rather than burying it in a
    ///     one-element aggregate, because the log line a maintainer reads is the inner message. That
    ///     every step still ran is guaranteed by the step helpers above; this only decides how what
    ///     failed is reported.
    /// </remarks>
    /// <param name="failures">Collected cleanup failures; an empty collection represents no recorded failure.</param>
    /// <returns>A wrapper retaining one or all failures, or null for an empty collection.</returns>
    internal static Exception? ShutdownFailure(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? null
            : new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failures.Combine("Multiple application shutdown steps were unverified."));
    }
}
