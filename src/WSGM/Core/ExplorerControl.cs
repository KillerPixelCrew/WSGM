using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Detect/start/kill explorer.exe within the current session.</summary>
public static class ExplorerControl
{
    // Explorer's own Ctrl+Shift taskbar "Exit Explorer" command — the ONLY exit
    // mechanism Winlogon accepts without an AutoRestartShell respawn. Undocumented,
    // so every use is bounded and fails open. The device evidence (kills and
    // Restart Manager both device-DISPROVEN) lives in docs\boot-and-shell.md.
    private const uint ExitExplorerMessage = 0x05B4;

    private static readonly Lock ExitGate = new();

    /// <summary>
    ///     The canonical Windows Explorer image path, shared by every launcher
    ///     and image-identity check.
    /// </summary>
    internal static string ExplorerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Gets whether Explorer is running in the current interactive session.</summary>
    public static bool IsRunningInSession()
    {
        return WindowFinder.FindProcessIds("explorer").Count > 0;
    }

    /// <summary>Starts Explorer for the current session when it is not already running.</summary>
    public static void StartExplorer()
    {
        StartExplorerCore(false);
    }

    /// <summary>
    ///     Starts Explorer and, when this process is elevated, BLOCKS until the
    ///     de-elevation check has run and repaired Explorer if needed.
    ///     <para>
    ///         For terminal recovery paths only — the crash-loop disarm and
    ///         <c>--restore-shell</c> both hand the user a desktop and then exit the process,
    ///         so the fire-and-forget verification <see cref="StartExplorer" /> queues would be
    ///         torn down before it ever ran, leaving an ELEVATED Explorer behind (which breaks
    ///         UWP: touch keyboard, Store apps; see <c>docs\elevation.md</c>). Costs the verification delay,
    ///         which is why the normal transition path keeps using
    ///         <see cref="StartExplorer" />. <c>Panic()</c> deliberately does NOT use this: that
    ///         process is already dying.
    ///     </para>
    /// </summary>
    public static void StartExplorerAndVerify()
    {
        StartExplorerCore(true);
    }

    /// <summary>Gets whether Explorer's desktop shell, not merely a folder window, runs in this session.</summary>
    /// <remarks>
    ///     Explorer is a per-session shell singleton, so starting it while its taskbar
    ///     exists only opens a File Explorer window. WSGM's own tray host also creates a
    ///     Shell_TrayWnd, which is why the owner must be the canonical explorer.exe.
    /// </remarks>
    private static bool IsDesktopShellRunning()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (!IsCurrentSessionWindow(taskbar))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(taskbar, out var owner);
        try
        {
            using var process = Process.GetProcessById(checked((int)owner));
            return string.Equals(process.MainModule?.FileName, ExplorerPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or Win32Exception or OverflowException)
        {
            return false;
        }
    }

    private static void StartExplorerCore(bool waitForElevationRepair)
    {
        try
        {
            if (IsDesktopShellRunning())
            {
                Log.Info("Explorer's desktop is already running; not starting another explorer.exe.");
                return;
            }

            var weAreElevated = ElevationCheck.IsCurrentProcessElevated() == true;

            Process.Start(new ProcessStartInfo(ExplorerPath) { UseShellExecute = true });
            Log.Info("Started explorer.exe");

            if (!weAreElevated)
            {
                return;
            }

            // Win11 explorer normally de-elevates itself through its own
            // scheduled task — but whether that survives a custom shell
            // registration is undocumented. Verify, and repair once if not:
            // an elevated explorer breaks UWP (touch keyboard, store apps).
            if (waitForElevationRepair)
            {
                // Blocking on purpose, and via Task.Run so the wait can never
                // deadlock against a captured context: the callers are terminal
                // recovery paths that exit the process immediately afterwards, so
                // an un-awaited verification would be torn down before it ran.
                Task.Run(VerifyAndRepairElevation).GetAwaiter().GetResult();
            }
            else
            {
                Task.Run(VerifyAndRepairElevation);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start explorer.exe", ex);
        }
    }

    private static async Task VerifyAndRepairElevation()
    {
        try
        {
            // The de-elevation hop goes through Task Scheduler; give it time to land.
            await Task.Delay(5000);

            var elevated = false;
            var undetermined = false;
            var seen = false;
            foreach (var pid in WindowFinder.FindProcessIds("explorer"))
            {
                try
                {
                    seen = true;
                    // Three states, not two: IsProcessElevated returns null when
                    // Windows would not answer, and folding that into "unelevated"
                    // reports a repair that never happened.
                    var state = ElevationCheck.IsProcessElevated(pid);
                    switch (state)
                    {
                        case true:
                            elevated = true;
                            break;
                        case null:
                            undetermined = true;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    undetermined = true;
                    Log.Warn($"Explorer elevation query failed: {ex.Message}");
                }
            }

            if (!seen)
            {
                Log.Warn("Explorer verification: no explorer process found 5 s after start.");
                return;
            }

            switch (elevated)
            {
                case false when undetermined:
                    // Restarting a shell we cannot even classify is worse than living
                    // with the possibility: leave it alone, but say so in the log.
                    Log.Warn("Explorer elevation could not be determined — leaving it alone.");
                    return;
                case false:
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

    /// <summary>
    ///     Requests orderly exit of the actual desktop shell, then releases a stuck original
    ///     process only after both shell surfaces have disappeared. Folder-only Explorer processes do
    ///     not own the desktop and do not block Game Mode. A replacement shell gets one orderly attempt.
    /// </summary>
    /// <param name="timeout">Total budget, including a replacement shell and readiness checks.</param>
    /// <returns>Whether the desktop shell is stably absent.</returns>
    public static bool ExitExplorerAndWait(TimeSpan timeout)
    {
        lock (ExitGate)
        {
            var deadline = DateTime.UtcNow + timeout;
            for (var attempt = 0; attempt < 2 && DateTime.UtcNow < deadline; attempt++)
            {
                var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                if (taskbar == 0)
                {
                    return WaitForShellAbsence(deadline);
                }

                if (!IsCurrentSessionWindow(taskbar))
                {
                    return false;
                }

                NativeMethods.GetWindowThreadProcessId(taskbar, out var owner);
                using var original = Process.GetProcessById(checked((int)owner));
                // Keep the handle, not merely the PID: PID reuse cannot authorize termination.
                _ = original.Handle;
                if (!string.Equals(original.MainModule?.FileName, ExplorerPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Log.Info($"Requesting orderly Explorer exit (pid {owner}).");
                if (!NativeMethods.PostMessageW(taskbar, ExitExplorerMessage, 0, 0))
                {
                    return false;
                }

                DateTime? absentSince = null;
                var replacement = false;
                while (DateTime.UtcNow < deadline)
                {
                    var currentTaskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                    var shell = NativeMethods.GetShellWindow();
                    var surfaces = currentTaskbar != 0 || shell != 0;
                    if (currentTaskbar != 0 && !IsWindowOwnedByProcess(currentTaskbar, owner))
                    {
                        replacement = true;
                        Log.Info("A replacement desktop appeared; requesting its orderly exit once.");
                        break;
                    }

                    absentSince = surfaces ? null : absentSince ?? DateTime.UtcNow;
                    var absent = absentSince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
                    var action = ExplorerExitPolicy.Decide(surfaces, original.HasExited, absent);
                    switch (action)
                    {
                        case ExplorerExitAction.Complete:
                            Log.Info("Explorer desktop exited and remained absent.");
                            return true;
                        case ExplorerExitAction.ReleaseOriginal:
                        {
                            // Orderly shutdown already removed both surfaces. A stuck extension must
                            // not strand the next Explorer behind the old process's shell singleton.
                            Log.Warn($"Releasing retired Explorer pid {owner} after orderly shell shutdown.");
                            original.Kill();
                            var remainingMs = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                            if (!original.WaitForExit(Math.Min(2000, remainingMs)))
                            {
                                return false;
                            }

                            absentSince = null; // Observe Winlogon after releasing the original process.
                            break;
                        }
                        case ExplorerExitAction.Wait:
                            break;
                    }

                    Thread.Sleep(100);
                }

                if (!replacement)
                {
                    break;
                }

                Thread.Sleep(300);
            }

            Log.Warn("Explorer desktop exit was not confirmed; desktop recovery is required.");
            return false;
        }
    }

    private static bool WaitForShellAbsence(DateTime deadline)
    {
        DateTime? absentSince = null;
        while (DateTime.UtcNow < deadline)
        {
            var present = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0
                          || NativeMethods.GetShellWindow() != 0;
            absentSince = present ? null : absentSince ?? DateTime.UtcNow;
            if (absentSince is { } since && DateTime.UtcNow - since >= TimeSpan.FromMilliseconds(500))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static bool IsWindowOwnedByProcess(nint window, uint processId)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var currentOwner);
        return currentOwner == processId;
    }

    private static bool IsCurrentSessionWindow(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.SessionId == WindowFinder.CurrentSessionId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Repair path only: kills the ELEVATED instances (an unelevated one is
    ///     what we want to keep) and waits — bounded — for them to actually die. Kill is
    ///     asynchronous, and explorer is a per-session singleton: starting the
    ///     replacement while the old instance still lives makes the new one open a
    ///     folder window instead of becoming the shell.
    /// </summary>
    private static void KillElevatedExplorerAndWait()
    {
        var killed = new List<Process>();
        foreach (var pid in WindowFinder.FindProcessIds("explorer"))
        {
            var isElevated = false;
            try
            {
                isElevated = ElevationCheck.IsProcessElevated(pid) == true;
            }
            catch (Exception)
            {
                // An unreadable process is treated as unelevated and left running.
            }

            if (!isElevated)
            {
                continue;
            }

            Process? p = null;
            try
            {
                p = Process.GetProcessById(checked((int)pid));
                Log.Info($"Killing ELEVATED explorer.exe (pid {pid})");
                p.Kill();
                killed.Add(p);
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and open.
                p?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not kill explorer pid {pid}: {ex.Message}");
                p?.Dispose();
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
            catch (Exception)
            {
                // Best effort: the process may already be gone or inaccessible.
            }
            finally
            {
                p.Dispose();
            }
        }
    }
}
