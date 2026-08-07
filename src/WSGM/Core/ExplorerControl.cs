using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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

    // Explorer's own Ctrl+Shift taskbar "Exit Explorer" command. This message is
    // undocumented, so every use is bounded and fails open if the current Windows
    // build does not honor it. Unlike Process.Kill, the orderly exit is not
    // treated by Winlogon as a shell crash requiring AutoRestartShell recovery —
    // kills got the shell respawned on device in both eras, and the Restart
    // Manager's end-session shutdown wedged a freshly logged-on explorer (~30 s
    // block, error 351) and got it respawned too. Do not bring either back.
    private const uint ExitExplorerMessage = 0x05B4;

    private static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(500);
    private static readonly object ExitGate = new();

    /// <summary>Requests Explorer's orderly shell exit and verifies boundedly that
    /// no current-session Explorer remains before a replacement tray is created.
    /// Fails OPEN: on refusal, timeout, or a Winlogon respawn the caller must
    /// preserve desktop mode — a replacement explorer is never killed (fighting
    /// AutoRestartShell just loops). Lingering snapshotted processes are
    /// terminated only after explorer already destroyed its taskbar (a shell
    /// extension can hold the process open — device-observed). Serialized so the
    /// boot takeover and an overlay mode switch can never race two exits.</summary>
    /// <param name="timeout">Total budget for the exit and the stability check.</param>
    /// <returns><see langword="true"/> only when Explorer exited without Winlogon
    /// immediately replacing it.</returns>
    public static bool ExitExplorerAndWait(TimeSpan timeout)
    {
        lock (ExitGate)
        {
            return ExitExplorerAndWaitCore(timeout);
        }
    }

    private static bool ExitExplorerAndWaitCore(TimeSpan timeout)
    {
        var initialProcessIds = ExplorerProcessIdsInSession();
        if (initialProcessIds.Count == 0)
        {
            return true;
        }

        var taskbar = Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (!IsCurrentSessionWindow(taskbar))
        {
            Log.Warn("Cannot request orderly Explorer exit: current-session taskbar was not found.");
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(taskbar, out var taskbarProcessId);
        if (!initialProcessIds.Contains(checked((int)taskbarProcessId)))
        {
            Log.Warn($"Cannot request orderly Explorer exit: taskbar owner pid {taskbarProcessId} is not Explorer.");
            return false;
        }

        Log.Info($"Requesting orderly Explorer exit (pid {taskbarProcessId}).");
        if (!Interop.NativeMethods.PostMessageW(taskbar, ExitExplorerMessage, 0, 0))
        {
            Log.Warn($"Orderly Explorer exit request failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var currentProcessIds = ExplorerProcessIdsInSession();
            var replacementProcessId = FindReplacementProcessId(initialProcessIds, currentProcessIds);
            if (replacementProcessId != 0)
            {
                Log.Warn($"Winlogon restarted Explorer as pid {replacementProcessId}; takeover cancelled.");
                return false;
            }
            if (currentProcessIds.Count == 0)
            {
                return WaitForStableExplorerAbsence(initialProcessIds, deadline);
            }
            if (!IsWindowOwnedByProcess(taskbar, taskbarProcessId))
            {
                Log.Info("Explorer acknowledged orderly exit and removed its taskbar.");
                break;
            }
            System.Threading.Thread.Sleep(100);
        }

        var taskbarStillPresent = IsWindowOwnedByProcess(taskbar, taskbarProcessId);
        var afterTaskbar = ExplorerProcessIdsInSession();
        var replacementAfterTaskbar = FindReplacementProcessId(initialProcessIds, afterTaskbar);
        // Lingering originals may be terminated only when the orderly exit was
        // acknowledged (taskbar destroyed) and Winlogon has not respawned a shell.
        if (taskbarStillPresent || replacementAfterTaskbar != 0)
        {
            Log.Warn(taskbarStillPresent
                ? "Explorer did not honor the orderly exit request before timeout."
                : $"Winlogon restarted Explorer as pid {replacementAfterTaskbar}; takeover cancelled.");
            return false;
        }

        // Explorer has already performed its intentional shell shutdown, but a
        // shell extension or folder window can keep the original process alive.
        // Terminate only those snapshotted PIDs; a replacement is never killed.
        System.Threading.Thread.Sleep(300);
        afterTaskbar = ExplorerProcessIdsInSession();
        replacementAfterTaskbar = FindReplacementProcessId(initialProcessIds, afterTaskbar);
        if (replacementAfterTaskbar != 0)
        {
            Log.Warn($"Winlogon restarted Explorer as pid {replacementAfterTaskbar}; takeover cancelled.");
            return false;
        }
        if (afterTaskbar.Count > 0)
        {
            Log.Warn($"Explorer taskbar exited but original process(es) {string.Join(", ", afterTaskbar)} lingered; terminating them.");
            TerminateOriginalExplorerProcesses(initialProcessIds);
        }
        return WaitForStableExplorerAbsence(initialProcessIds, deadline);
    }

    /// <summary>Success only after half a second of continuous explorer absence —
    /// a Winlogon respawn shows up within that window and cancels the takeover.</summary>
    private static bool WaitForStableExplorerAbsence(IReadOnlyCollection<int> initialProcessIds, DateTime deadline)
    {
        var stableSinceUtc = (DateTime?)null;
        while (DateTime.UtcNow < deadline)
        {
            var currentProcessIds = ExplorerProcessIdsInSession();
            var replacementProcessId = FindReplacementProcessId(initialProcessIds, currentProcessIds);
            if (replacementProcessId != 0)
            {
                Log.Warn($"Winlogon restarted Explorer as pid {replacementProcessId}; takeover cancelled.");
                return false;
            }
            if (currentProcessIds.Count == 0)
            {
                stableSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSinceUtc.Value >= StableAbsence)
                {
                    Log.Info("Explorer exited cleanly without replacement.");
                    return true;
                }
            }
            else
            {
                stableSinceUtc = null;
            }
            System.Threading.Thread.Sleep(100);
        }
        Log.Warn("Explorer processes did not exit cleanly before timeout.");
        return false;
    }

    private static void TerminateOriginalExplorerProcesses(IEnumerable<int> originalProcessIds)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var processId in originalProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.SessionId != sessionId ||
                    !process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Log.Info($"Terminating lingering explorer.exe (pid {processId}) after orderly shell exit.");
                process.Kill();
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and open.
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not terminate lingering explorer pid {processId}: {ex.Message}");
            }
        }
    }

    private static List<int> ExplorerProcessIdsInSession()
    {
        var ids = new List<int>();
        foreach (var p in ExplorerInSession())
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    /// <summary>Any current explorer PID that was not in the initial snapshot is a
    /// Winlogon replacement (0 = none).</summary>
    private static int FindReplacementProcessId(
        IReadOnlyCollection<int> initialProcessIds, IReadOnlyCollection<int> currentProcessIds)
    {
        foreach (var id in currentProcessIds)
        {
            if (!initialProcessIds.Contains(id))
            {
                return id;
            }
        }
        return 0;
    }

    private static bool IsWindowOwnedByProcess(nint window, uint processId)
    {
        if (window == 0 || !Interop.NativeMethods.IsWindow(window))
        {
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(window, out var currentOwner);
        return currentOwner == processId;
    }

    private static bool IsCurrentSessionWindow(nint window)
    {
        if (window == 0)
        {
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.SessionId == Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            return false;
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
