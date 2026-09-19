using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     Starts a process with the interactive user's medium-IL token from an
///     elevated WSGM, by registering and running a one-shot scheduled task with
///     LogonType=InteractiveToken and default (least) run level — the same mechanism
///     Windows 11's own explorer uses to de-elevate itself (CreateExplorerShellUnelevatedTask).
///     The naive TokenLinkedToken route does NOT work here: without SeTcbPrivilege the
///     linked token is only a SecurityIdentification impersonation token and cannot be
///     converted to a primary token (fails with ERROR_BAD_IMPERSONATION_LEVEL) — verified
///     empirically. When UAC is disabled entirely there is no limited token to run as and
///     this (like every technique) cannot help.
/// </summary>
internal static class UnelevatedLauncher
{
    public static bool TryStartViaScheduledTask(string exePath, string arguments = "")
    {
        // Recovery and legacy synchronous callers share the exact bounded implementation used by
        // the asynchronous desktop handoff. ConfigureAwait(false) throughout keeps this fixed sync
        // boundary independent of a UI synchronization context.
        var disposition = TryStartViaScheduledTaskAsync(
            exePath,
            arguments,
            DateTimeOffset.UtcNow.AddSeconds(30)).GetAwaiter().GetResult();
        return disposition is ScheduledTaskLaunchDisposition.Dispatched;
    }

    /// <summary>
    ///     Runs the scheduled-task handoff within a caller-owned absolute deadline. Task
    ///     creation, dispatch, and best-effort deletion all consume the same remaining budget.
    /// </summary>
    internal static async Task<ScheduledTaskLaunchDisposition> TryStartViaScheduledTaskAsync(
        string exePath,
        string arguments,
        DateTimeOffset deadline,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var suffix = $"{Environment.ProcessId}-{Random.Shared.Next():x8}";
        var taskName = $"WSGM_StartUnelevated_{suffix}";
        var xmlPath = Path.Combine(Log.Directory, $"wsgm-task-{suffix}.xml");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline))
            {
                Log.Warn("De-elevated scheduled-task launch was not started because its shared deadline expired.");
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            Directory.CreateDirectory(Log.Directory);
            var taskXml = BuildTaskXml(exePath, arguments, workingDirectory);
            using (var writeCancellation = CreateBudgetCancellation(deadline, cancellationToken))
            {
                await File.WriteAllTextAsync(
                    xmlPath,
                    taskXml,
                    Encoding.Unicode,
                    writeCancellation.Token).ConfigureAwait(false);
            }

            var disposition = await RunScheduledTaskSequenceAsync(
                taskName,
                xmlPath,
                deadline,
                RunSchtasksUntilAsync,
                cancellationToken).ConfigureAwait(false);
            if (disposition is ScheduledTaskLaunchDisposition.Dispatched)
            {
                Log.Info($"Started via de-elevating scheduled task: {exePath}"
                         + (arguments.Length == 0 ? "" : $" {arguments}"));
            }

            return disposition;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("De-elevated scheduled-task launch did not finish before its shared deadline.");
            return ScheduledTaskLaunchDisposition.NotDispatched;
        }
        catch (Exception ex)
        {
            Log.Error("De-elevated launch via scheduled task failed", ex);
            return ScheduledTaskLaunchDisposition.NotDispatched;
        }
        finally
        {
            // Local deletion does not consume the Task Scheduler budget. Always remove the input
            // document even when dispatch was cancelled or timed out; otherwise every bounded
            // recovery failure leaves another XML file in the durable log directory.
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"Scheduled-task XML cleanup failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Runs create, dispatch, and cleanup commands through an injected runner so their
    ///     shared-deadline contract can be verified without invoking Task Scheduler.
    /// </summary>
    internal static Task<ScheduledTaskLaunchDisposition> RunScheduledTaskSequenceAsync(
        string taskName,
        string xmlPath,
        DateTimeOffset deadline,
        Func<string, DateTimeOffset, CancellationToken, Task<ConsoleToolRunOutcome>> runCommand,
        CancellationToken cancellationToken)
    {
        return RunScheduledTaskSequenceAsync(
            taskName,
            xmlPath,
            deadline,
            runCommand,
            static () => DateTimeOffset.UtcNow,
            cancellationToken);
    }

#pragma warning disable CA2219
    /// <summary>
    ///     Runs the scheduled-task sequence through an injected clock so deadline closure can
    ///     be verified deterministically without invoking Task Scheduler.
    /// </summary>
    internal static async Task<ScheduledTaskLaunchDisposition> RunScheduledTaskSequenceAsync(
        string taskName,
        string xmlPath,
        DateTimeOffset deadline,
        Func<string, DateTimeOffset, CancellationToken, Task<ConsoleToolRunOutcome>> runCommand,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runCommand);
        ArgumentNullException.ThrowIfNull(utcNow);
        var taskMayExist = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline, utcNow))
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            var create = await runCommand(
                $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F",
                deadline,
                cancellationToken).ConfigureAwait(false);
            taskMayExist = create is ConsoleToolRunOutcome.Succeeded
                or ConsoleToolRunOutcome.Unknown;
            if (create is not ConsoleToolRunOutcome.Succeeded)
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline, utcNow))
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            try
            {
                var run = await runCommand(
                    $"/Run /TN \"{taskName}\"",
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return run switch
                {
                    ConsoleToolRunOutcome.Succeeded => ScheduledTaskLaunchDisposition.Dispatched,
                    ConsoleToolRunOutcome.Unknown => ScheduledTaskLaunchDisposition.Unknown,
                    _ => ScheduledTaskLaunchDisposition.NotDispatched
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Once /Run begins, an exception cannot prove Task Scheduler rejected the
                // request. The caller must suppress every competing shell launch surface.
                Log.Warn($"Scheduled-task dispatch result is unknown for {taskName}: {ex.Message}");
                return ScheduledTaskLaunchDisposition.Unknown;
            }
        }
        finally
        {
            if (taskMayExist)
            {
                if (cancellationToken.IsCancellationRequested || !HasRemainingBudget(deadline, utcNow))
                {
                    Log.Warn($"Scheduled-task cleanup skipped for {taskName}: the shared budget is closed.");
                }
                else
                {
                    try
                    {
                        var deleted = await runCommand(
                            $"/Delete /TN \"{taskName}\" /F",
                            deadline,
                            cancellationToken).ConfigureAwait(false);
                        if (deleted is not ConsoleToolRunOutcome.Succeeded)
                        {
                            Log.Warn($"Scheduled-task cleanup failed for {taskName}.");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Scheduled-task cleanup failed for {taskName}: {ex.Message}");
                    }
                }
            }
        }
    }
#pragma warning restore CA2219

    internal static string BuildTaskXml(string exePath, string arguments = "", string? workingDirectory = null)
    {
        return ScheduledTaskXml.Build(exePath, arguments, workingDirectory);
    }

    private static Task<ConsoleToolRunOutcome> RunSchtasksUntilAsync(
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        return ConsoleTool.RunUntilAsync(
            ConsoleTool.System32("schtasks.exe"),
            arguments,
            deadline,
            cancellationToken);
    }

    private static CancellationTokenSource CreateBudgetCancellation(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - DateTimeOffset.UtcNow;
        source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return source;
    }

    private static bool HasRemainingBudget(DateTimeOffset deadline)
    {
        return deadline > DateTimeOffset.UtcNow;
    }

    private static bool HasRemainingBudget(
        DateTimeOffset deadline,
        Func<DateTimeOffset> utcNow)
    {
        return deadline > utcNow();
    }
}

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
