// Shared by WSGM and WSGM.Setup. Steam lifetime policy has no UI or application logger dependency.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shared;

/// <summary>Completes a requested Steam shutdown with bounded graceful and forced phases.</summary>
internal static class SteamProcessControl
{
    internal const string ExitUrl = "steam://exit";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Stops Steam and its CEF helpers in this session, then verifies that they exited.</summary>
    /// <param name="budget">The total shutdown budget, including forced-exit verification.</param>
    /// <param name="gracefulBudget">How long Steam may ignore the protocol exit before being terminated.</param>
    /// <param name="log">Receives transition and failure diagnostics.</param>
    /// <param name="cancellationToken">Cancels waiting and prevents a later forced stop.</param>
    /// <param name="entireProcessTree">Whether a full-close request also terminates Steam's child processes.</param>
    /// <returns>True only when Steam and its helpers are absent from this session.</returns>
    internal static async Task<bool> StopAsync(TimeSpan budget, TimeSpan gracefulBudget,
        Action<string> log, CancellationToken cancellationToken, bool entireProcessTree = false)
    {
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        var clock = Stopwatch.StartNew();
        var stopped = await StopAsync(budget, gracefulBudget,
            () => IsRunning(session),
            () =>
            {
                if (!IsRunning(session, mainOnly: true))
                {
                    // Dispatching a protocol with only orphaned helpers left would start a new
                    // client just to close it. Those helpers can be ended directly instead.
                    log("Only Steam's CEF helpers remain; proceeding to forced shutdown.");
                    return false;
                }

                try
                {
                    log("Requesting Steam exit (steam://exit).");
                    using var request = Process.Start(new ProcessStartInfo(ExitUrl) { UseShellExecute = true });
                    // ShellExecute may service the URL in an existing process and return no wrapper.
                    return true;
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
                {
                    log($"Steam exit request failed: {ex.Message}; proceeding to forced shutdown.");
                    return false;
                }
            },
            () => ForceStop(session, log, entireProcessTree),
            () => clock.Elapsed,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken).ConfigureAwait(false);
        log(stopped ? "Steam shutdown confirmed." : "Steam shutdown was not confirmed within its budget.");
        return stopped;
    }

    /// <summary>Runs the same shutdown ordering with explicit process and monotonic-clock operations.</summary>
    /// <param name="budget">Total graceful and forced verification budget.</param>
    /// <param name="gracefulBudget">Time reserved for orderly exit.</param>
    /// <param name="isRunning">Fresh presence of Steam or its helpers.</param>
    /// <param name="requestExit">Requests orderly exit; false skips the graceful wait.</param>
    /// <param name="forceStop">Terminates the remaining owned Steam processes once.</param>
    /// <param name="elapsed">Monotonic clock shared by both phases.</param>
    /// <param name="delay">Cancellation-aware wait.</param>
    /// <param name="cancellationToken">Prevents effects and interrupts waits.</param>
    /// <returns>True only after absence was observed, including when Steam was already closed.</returns>
    internal static async Task<bool> StopAsync(TimeSpan budget, TimeSpan gracefulBudget,
        Func<bool> isRunning, Func<bool> requestExit, Action forceStop, Func<TimeSpan> elapsed,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(gracefulBudget, budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!isRunning())
        {
            return true;
        }

        var start = elapsed();
        var deadline = start + budget;
        if (requestExit() && await WaitForExitAsync(start + gracefulBudget).ConfigureAwait(false))
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!isRunning())
        {
            return true;
        }

        if (elapsed() >= deadline)
        {
            return false;
        }

        forceStop();
        return await WaitForExitAsync(deadline).ConfigureAwait(false);

        async Task<bool> WaitForExitAsync(TimeSpan until)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isRunning())
                {
                    return true;
                }

                var remaining = until - elapsed();
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsRunning(int session, bool mainOnly = false)
    {
        var processes = ProcessesInSession(session, mainOnly);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Count > 0;
    }

    private static List<Process> ProcessesInSession(int session, bool mainOnly = false)
    {
        var matches = new List<Process>();
        var names = mainOnly ? new[] { "steam" } : new[] { "steam", "steamwebhelper" };
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.SessionId == session)
                    {
                        matches.Add(process);
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited during the snapshot.
                }

                process.Dispose();
            }
        }

        return matches;
    }

    private static void ForceStop(int session, Action<string> log, bool entireProcessTree)
    {
        foreach (var process in ProcessesInSession(session))
        {
            using (process)
            {
                try
                {
                    log($"Steam did not exit; terminating {process.ProcessName} pid {process.Id} in session {session} "
                        + $"(entireProcessTree={entireProcessTree}).");
                    try
                    {
                        process.Kill(entireProcessTree);
                    }
                    catch (AggregateException ex)
                    {
                        log($"Steam child termination reported errors for pid {process.Id}: {ex.Message}; "
                            + "terminating the client itself.");
                        process.Kill(entireProcessTree: false);
                    }
                }
                catch (InvalidOperationException)
                {
                    // It exited between observation and termination.
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or NotSupportedException)
                {
                    log($"Steam forced shutdown failed for pid {process.Id}: {ex.Message}");
                }
            }
        }
    }
}
