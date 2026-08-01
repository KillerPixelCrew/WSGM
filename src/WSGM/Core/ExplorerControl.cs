using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Detect/start/kill explorer.exe within the current session.</summary>
public static class ExplorerControl
{
    /// <summary>Gets whether Explorer is running in the current interactive session.</summary>
    public static bool IsRunningInSession()
    {
        var procs = ExplorerInSession();
        foreach (var p in procs)
        {
            p.Dispose();
        }
        return procs.Count > 0;
    }

    /// <summary>Starts Explorer for the current session when it is not already running.</summary>
    public static void StartExplorer()
    {
        try
        {
            var weAreElevated = ElevationCheck.IsCurrentProcessElevated() == true;

            Process.Start(new ProcessStartInfo(ExplorerPath) { UseShellExecute = true });
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

            var elevated = false;
            var seen = false;
            foreach (var p in ExplorerInSession())
            {
                try
                {
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
            KillElevatedExplorerAndWait();
            if (!UnelevatedLauncher.TryStartViaScheduledTask(ExplorerPath))
            {
                // Last resort: an elevated desktop beats no desktop.
                Log.Warn("De-elevated restart failed — starting explorer elevated. " +
                         "UWP features (touch keyboard, store apps) may misbehave.");
                Process.Start(new ProcessStartInfo(ExplorerPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Explorer elevation verification failed", ex);
        }
    }

    /// <summary>Terminates Explorer processes in the current session to return to game mode.</summary>
    public static void KillExplorer()
    {
        foreach (var p in ExplorerInSession())
        {
            try
            {
                Log.Info($"Killing explorer.exe (pid {p.Id})");
                p.Kill();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not kill explorer pid {p.Id}: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Repair path only: kills the ELEVATED instances (an unelevated one is
    /// what we want to keep) and waits — bounded — for them to actually die. Kill is
    /// asynchronous, and explorer is a per-session singleton: starting the
    /// replacement while the old instance still lives makes the new one open a
    /// folder window instead of becoming the shell.</summary>
    private static void KillElevatedExplorerAndWait()
    {
        var killed = new List<Process>();
        foreach (var p in ExplorerInSession())
        {
            var isElevated = false;
            try
            {
                isElevated = ElevationCheck.IsProcessElevated((uint)p.Id) == true;
            }
            catch { }
            if (!isElevated)
            {
                p.Dispose();
                continue;
            }
            try
            {
                Log.Info($"Killing ELEVATED explorer.exe (pid {p.Id})");
                p.Kill();
                killed.Add(p);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not kill explorer pid {p.Id}: {ex.Message}");
                p.Dispose();
            }
        }
        foreach (var p in killed)
        {
            try
            {
                if (!p.WaitForExit(5000))
                {
                    Log.Warn($"Explorer pid {p.Id} did not exit within 5 s — replacement may race it.");
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static string ExplorerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Explorer processes of the CURRENT session only (other RDP/FUS
    /// sessions run their own). Caller disposes every returned process.</summary>
    private static List<Process> ExplorerInSession()
    {
        var result = new List<Process>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            var keep = false;
            try
            {
                keep = p.SessionId == session;
            }
            catch { /* process may have exited */ }
            if (keep)
            {
                result.Add(p);
            }
            else
            {
                p.Dispose();
            }
        }
        return result;
    }
}
