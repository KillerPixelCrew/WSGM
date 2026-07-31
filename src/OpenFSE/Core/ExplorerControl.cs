using System;
using System.Diagnostics;
using System.IO;

namespace OpenFSE.Core;

/// <summary>Detect/start/kill explorer.exe within the current session.</summary>
public static class ExplorerControl
{
    public static bool IsRunningInSession()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (p.SessionId == session)
                {
                    return true;
                }
            }
            catch { /* process may have exited */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    public static void StartExplorer()
    {
        try
        {
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var weAreElevated = ElevationCheck.IsProcessElevated((uint)Environment.ProcessId) == true;

            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true });
            Log.Info("Started explorer.exe");

            if (weAreElevated)
            {
                // Win11 explorer normally de-elevates itself through its own
                // scheduled task — but whether that survives a custom shell
                // registration is undocumented. Verify, and repair once if not:
                // an elevated explorer breaks UWP (touch keyboard, store apps).
                System.Threading.Tasks.Task.Run(VerifyAndRepairElevation);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start explorer.exe", ex);
        }
    }

    private static async System.Threading.Tasks.Task VerifyAndRepairElevation()
    {
        try
        {
            // The de-elevation hop goes through Task Scheduler; give it time to land.
            await System.Threading.Tasks.Task.Delay(5000);

            var session = Process.GetCurrentProcess().SessionId;
            var elevated = false;
            var seen = false;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != session)
                    {
                        continue;
                    }
                    seen = true;
                    elevated |= ElevationCheck.IsProcessElevated((uint)p.Id) == true;
                }
                catch { }
                finally { p.Dispose(); }
            }

            if (!seen)
            {
                Log.Warn("Explorer verification: no explorer process found 5 s after start.");
                return;
            }
            if (!elevated)
            {
                Log.Info("Explorer is running unelevated (self-demotion worked).");
                return;
            }

            Log.Warn("Explorer is running ELEVATED — restarting it via de-elevating scheduled task.");
            KillExplorer();
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!UnelevatedLauncher.TryStartViaScheduledTask(explorer))
            {
                // Last resort: an elevated desktop beats no desktop.
                Log.Warn("De-elevated restart failed — starting explorer elevated. " +
                         "UWP features (touch keyboard, store apps) may misbehave.");
                Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Explorer elevation verification failed", ex);
        }
    }

    public static void KillExplorer()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (p.SessionId == session)
                {
                    Log.Info($"Killing explorer.exe (pid {p.Id})");
                    p.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not kill explorer pid {p.Id}: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
    }
}
