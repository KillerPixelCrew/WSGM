using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One home for the "run a hidden console tool and wait" pattern
/// (schtasks, powercfg, powershell one-shots), so every caller gets the same
/// exit-code and timeout checks — a timeout counts as failure, since reading
/// ExitCode from a still-running process would throw.</summary>
internal static class ConsoleTool
{
    // How long a killed tool's output pipes may take to close before the
    // captured output is given up on.
    private const int DrainTimeoutMs = 2000;

    /// <summary>True only when the tool started, exited within the timeout, and
    /// returned 0. Never throws; failures are logged with the leading argument so
    /// pasted logs show WHICH invocation failed.</summary>
    public static bool Run(string exe, string arguments, int timeoutMs = 15_000)
    {
        var what = $"{exe} {FirstToken(arguments)}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            });
            if (p is null)
            {
                Log.Warn($"{what} did not start.");
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                Log.Warn($"{what} still running after {timeoutMs / 1000} s — treated as failed.");
                return false;
            }
            if (p.ExitCode != 0)
            {
                Log.Warn($"{what} exited with {p.ExitCode}.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs a hidden console tool, captures its combined stdout/stderr,
    /// and returns the exit code — for tools whose OUTPUT matters (diskpart).
    /// A timeout kills the process tree and reports exit code -1. Never throws.</summary>
    /// <param name="exe">The executable to run.</param>
    /// <param name="arguments">Its command line.</param>
    /// <param name="timeoutMs">How long the tool may run.</param>
    public static async Task<(int ExitCode, string Output)> RunCapturedAsync(
        string exe, string arguments, int timeoutMs)
    {
        var what = $"{exe} {FirstToken(arguments)}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            });
            if (p is null)
            {
                Log.Warn($"{what} did not start.");
                return (-1, "");
            }
            // Read both streams concurrently — a tool that fills one pipe while
            // the caller waits on the other deadlocks otherwise.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"{what} still running after {timeoutMs / 1000} s — killing it.");
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"{what} could not be killed: {ex.Message}");
                }
                // A read completes only once every writer handle on the pipe is
                // gone, so a kill that failed (or a child still holding the
                // inherited handle) would leave these awaits pending forever and
                // hang the caller. Bound the drain: the documented contract is
                // (-1, output), never a wait without end.
                var drain = Task.WhenAll(stdout, stderr);
                if (await Task.WhenAny(drain, Task.Delay(DrainTimeoutMs)) != drain)
                {
                    Log.Warn($"{what} output could not be drained after the kill.");
                    return (-1, "");
                }
                return (-1, $"{await stdout}{await stderr}");
            }
            var output = $"{await stdout}{await stderr}";
            if (p.ExitCode != 0)
            {
                Log.Warn($"{what} exited with {p.ExitCode}.");
            }
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return (-1, "");
        }
    }

    /// <summary>Absolute System32 path for a Windows console tool. A relative exe
    /// name is resolved from the application directory first, which for a per-user
    /// install is user-writable — an elevated caller must never search it.</summary>
    public static string System32(string exeName) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);

    internal static string FirstToken(string arguments)
    {
        var space = arguments.IndexOf(' ');
        return space < 0 ? arguments : arguments[..space];
    }
}
