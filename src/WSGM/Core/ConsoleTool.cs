using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     Bounded console-process outcome preserving whether process start crossed an uncertain
///     side-effect boundary.
/// </summary>
internal enum ConsoleToolRunOutcome
{
    /// <summary>The process was never started.</summary>
    NotStarted,

    /// <summary>The process exited successfully before the deadline.</summary>
    Succeeded,

    /// <summary>The process exited with a known failure before the deadline.</summary>
    Failed,

    /// <summary>The process started but its result could not be verified.</summary>
    Unknown
}

/// <summary>One console invocation, including uncertain outcomes after process start.</summary>
internal sealed record ConsoleToolResult(ConsoleToolRunOutcome Outcome, int? ExitCode, string Output);

/// <summary>
///     Narrow owned-process surface used to verify bounded console-tool cleanup without
///     starting a live operating-system command from tests.
/// </summary>
internal interface IConsoleToolProcess : IDisposable
{
    /// <summary>Gets the process exit code after exit.</summary>
    int ExitCode { get; }

    /// <summary>Captures stdout and stderr concurrently.</summary>
    Task<string> ReadOutputAsync();

    /// <summary>Waits for the exact process to exit.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Requests termination of the exact process and its descendants.</summary>
    void KillTree();
}

/// <summary>
///     One home for the "run a hidden console tool and wait" pattern
///     (schtasks, powercfg, powershell one-shots), so every caller gets the same
///     exit-code and timeout checks. The absolute-deadline path preserves a timeout as
///     unknown after process start, because a side-effecting command may already have crossed its
///     dispatch boundary even though reading ExitCode from the still-running process is impossible.
/// </summary>
internal static class ConsoleTool
{
    // How long a killed tool's output pipes may take to close before the
    // captured output is given up on.
    private const int DrainTimeoutMs = 2000;

    internal static Task<ConsoleToolResult> RunAsync(string exe, string arguments, int timeoutMs = 15_000,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(exe, arguments, DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs), cancellationToken);
    }

    /// <summary>
    ///     Runs a hidden console tool within a caller-owned absolute deadline. The process
    ///     tree is stopped when that budget expires or the caller cancels, so sequential recovery
    ///     commands cannot each acquire a fresh timeout.
    /// </summary>
    /// <param name="exe">The executable to run.</param>
    /// <param name="arguments">Its command line.</param>
    /// <param name="deadline">The shared absolute deadline for the surrounding workflow.</param>
    /// <param name="cancellationToken">Cancels the command and stops its process tree.</param>
    /// <returns>
    ///     Whether the tool did not start, completed successfully, completed with a known
    ///     failure, or crossed process start without a verifiable result.
    /// </returns>
    internal static Task<ConsoleToolResult> RunAsync(
        string exe,
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            exe,
            arguments,
            deadline,
            static startInfo =>
            {
                var process = Process.Start(startInfo);
                return process is null ? null : new SystemConsoleToolProcess(process);
            },
            cancellationToken);
    }

    /// <summary>
    ///     Runs through an injected process owner so process-start, wait-fault, and cleanup
    ///     boundaries can be verified without invoking a live console tool.
    /// </summary>
    internal static async Task<ConsoleToolResult> RunAsync(
        string exe,
        string arguments,
        DateTimeOffset deadline,
        Func<ProcessStartInfo, IConsoleToolProcess?> startProcess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        var what = $"{exe} {FirstToken(arguments)}";
        var processStarted = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline <= DateTimeOffset.UtcNow)
        {
            Log.Warn($"{what} was not started because the shared deadline expired.");
            return new ConsoleToolResult(ConsoleToolRunOutcome.NotStarted, null, "");
        }

        try
        {
            using var process = startProcess(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            });
            if (process is null)
            {
                Log.Warn($"{what} did not start.");
                return new ConsoleToolResult(ConsoleToolRunOutcome.NotStarted, null, "");
            }

            processStarted = true;

            var remaining = deadline - DateTimeOffset.UtcNow;
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warn($"{what} did not finish before the shared deadline — killing it.");
                TryKill(process, what);
                await WaitForKilledProcessAsync(
                    process,
                    what,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return new ConsoleToolResult(ConsoleToolRunOutcome.Unknown, null, await ReadOutputAsync(process, deadline).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                TryKill(process, what);
                throw;
            }
            catch (Exception ex)
            {
                // Disposing Process closes only our handle; it does not terminate the process.
                // Once start crossed the side-effect boundary, an unobservable wait must still
                // retire the exact tool tree before the caller continues with later recovery work.
                // Kill is asynchronous, so wait for the exact process under the same deadline;
                // otherwise a following schtasks /Delete can race a still-finishing /Create.
                Log.Warn($"{what} wait failed after process start — killing it: {ex.Message}");
                TryKill(process, what);
                await WaitForKilledProcessAsync(
                    process,
                    what,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return new ConsoleToolResult(ConsoleToolRunOutcome.Unknown, null, await ReadOutputAsync(process, deadline).ConfigureAwait(false));
            }

            if (process.ExitCode == 0)
            {
                return new ConsoleToolResult(ConsoleToolRunOutcome.Succeeded, process.ExitCode, await ReadOutputAsync(process, deadline).ConfigureAwait(false));
            }

            Log.Warn($"{what} exited with {process.ExitCode}.");
            return new ConsoleToolResult(ConsoleToolRunOutcome.Failed, process.ExitCode, await ReadOutputAsync(process, deadline).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return new ConsoleToolResult(processStarted ? ConsoleToolRunOutcome.Unknown : ConsoleToolRunOutcome.NotStarted, null, "");
        }
    }

    private static async Task<string> ReadOutputAsync(IConsoleToolProcess process, DateTimeOffset deadline)
    {
        var output = process.ReadOutputAsync();
        Log.Observe(output, "Console output capture");
        var remaining = deadline - DateTimeOffset.UtcNow;
        try
        {
            if (output.IsCompleted)
            {
                return await output.ConfigureAwait(false);
            }

            return remaining > TimeSpan.Zero
                ? await output.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(DrainTimeoutMs, remaining.TotalMilliseconds))).ConfigureAwait(false)
                : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Console output could not be drained: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    ///     Absolute System32 path for a Windows console tool. A relative exe
    ///     name is resolved from the application directory first, which for a per-user
    ///     install is user-writable — an elevated caller must never search it.
    /// </summary>
    public static string System32(string exeName)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
    }

    internal static string FirstToken(string arguments)
    {
        var space = arguments.IndexOf(' ');
        return space < 0 ? arguments : arguments[..space];
    }

    private static void TryKill(IConsoleToolProcess process, string what)
    {
        try
        {
            // Kill is safe to attempt unconditionally: an already-exited Process reports
            // InvalidOperationException, while querying HasExited first can itself fail and
            // must not turn a wait fault into an unretired side-effecting process.
            process.KillTree();
        }
        catch (InvalidOperationException)
        {
            // The process already exited before the kill reached it.
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} could not be killed: {ex.Message}");
        }
    }

    private static async Task WaitForKilledProcessAsync(
        IConsoleToolProcess process,
        string what,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        using var exitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exitCancellation.CancelAfter(remaining);
        try
        {
            await process.WaitForExitAsync(exitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"{what} termination could not be confirmed before the shared deadline.");
            await DelayUntilDeadlineAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A second wait fault still must not let a later side-effecting command run beside this
            // process. Consume the remaining shared budget; its caller will then skip cleanup.
            Log.Warn($"{what} termination wait failed: {ex.Message}");
            await DelayUntilDeadlineAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DelayUntilDeadlineAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining;
        while ((remaining = deadline - DateTimeOffset.UtcNow) > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SystemConsoleToolProcess : IConsoleToolProcess
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        internal SystemConsoleToolProcess(Process process)
        {
            _process = process;
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public int ExitCode => _process.ExitCode;

        public async Task<string> ReadOutputAsync()
        {
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
            return _stdout.Result + _stderr.Result;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _process.WaitForExitAsync(cancellationToken);
        }

        public void KillTree()
        {
            _process.Kill(true);
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }
}
