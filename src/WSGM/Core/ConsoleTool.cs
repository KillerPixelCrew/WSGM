using System;
using System.Diagnostics;

namespace WSGM.Core;

/// <summary>One home for the "run a hidden console tool and wait" pattern
/// (schtasks, powercfg, powershell one-shots), so every caller gets the same
/// exit-code and timeout checks — a timeout counts as failure, since reading
/// ExitCode from a still-running process would throw.</summary>
internal static class ConsoleTool
{
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

    internal static string FirstToken(string arguments)
    {
        var space = arguments.IndexOf(' ');
        return space < 0 ? arguments : arguments[..space];
    }
}
