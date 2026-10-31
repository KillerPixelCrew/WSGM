using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Owns the single process exit attempt independently of Avalonia's shutdown events.</summary>
/// <param name="sessionShutdown">Optional session cleanup receiving its policy and absolute UTC deadline.</param>
/// <param name="forcedExit">
///     Terminates the process with the selected exit code unless Windows is already ending the
///     session.
/// </param>
/// <param name="reportHandoff">
///     Publishes the outcome after the bounded cleanup attempt; called once even if late cleanup
///     continues.
/// </param>
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

    /// <summary>Gets the token cancelled when <see cref="Deadline" /> passes, including after it tightens.</summary>
    /// <remarks>
    ///     Read it while the exit attempt runs: its source is disposed once the attempt completes, and a
    ///     token read before that stays usable and cancelled.
    /// </remarks>
    internal CancellationToken DeadlineToken => _timeout.Token;

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

            var reason = ApplicationShutdownRequest.Effective;
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
        var reason = ApplicationShutdownRequest.Effective;
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
                var reason = ApplicationShutdownRequest.Effective;
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
