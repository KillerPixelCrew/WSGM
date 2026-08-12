using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Invokes explicit user-requested Windows power operations.</summary>
public static class PowerActions
{
    /// <summary>Puts the device to sleep. The suspend runs off the caller's thread:
    /// SetSuspendState does not return until the system resumes, and the quick-access
    /// panel's deferred close (invariant 3) needs its dispatcher back immediately.</summary>
    public static void Sleep()
    {
        Log.Info("Power: sleep");
        _ = Task.Run(() =>
        {
            if (!NativeMethods.SetSuspendState(false, false, false))
            {
                Log.Error($"SetSuspendState failed (error {Marshal.GetLastPInvokeError()})");
            }
        });
    }

    /// <summary>Restarts Windows.</summary>
    public static void Restart()
    {
        Log.Info("Power: restart");
        RunShutdown("/r /t 0");
    }

    /// <summary>Shuts Windows down.</summary>
    public static void Shutdown()
    {
        Log.Info("Power: shutdown");
        RunShutdown("/s /t 0");
    }

    private static void RunShutdown(string arguments)
    {
        try
        {
            // Absolute System32 path: an elevated shell must never resolve a system
            // tool through the CreateProcess search order (ConsoleTool.System32).
            var p = Process.Start(new ProcessStartInfo(ConsoleTool.System32("shutdown.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                Log.Error($"shutdown.exe {arguments} did not start");
                return;
            }
            // A successful shutdown never gets here (the machine goes down), so a
            // reported exit code always means the request was refused — log it, the
            // device log is the only diagnosis surface.
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                if (p.ExitCode != 0)
                {
                    Log.Error($"shutdown.exe {arguments} exited with {p.ExitCode}");
                }
                p.Dispose();
            };
        }
        catch (Exception ex)
        {
            Log.Error($"shutdown.exe {arguments} failed", ex);
        }
    }
}
