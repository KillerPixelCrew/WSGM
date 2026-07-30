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
            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true });
            Log.Info("Started explorer.exe");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start explorer.exe", ex);
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
