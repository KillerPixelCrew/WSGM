using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Observes the desktop shell and enters the shared terminal recovery path.</summary>
public static class ExplorerControl
{
    /// <summary>
    ///     The canonical Windows Explorer image path, shared by every launcher
    ///     and image-identity check.
    /// </summary>
    internal static string ExplorerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Restores the desktop through the same observed launch path as a session return.</summary>
    public static void StartExplorerAndVerify(UserDataContext context)
    {
        RestoreTerminalAsync(context).GetAwaiter().GetResult();
    }

    private static async Task RestoreTerminalAsync(UserDataContext context)
    {
        try
        {
            if (IsDesktopShellRunning())
            {
                return;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            var disposition = await ExplorerLauncher.StartAsync(context, deadline, default).ConfigureAwait(false);
            if (disposition == ScheduledTaskLaunchDisposition.NotDispatched)
            {
                Log.Warn("Terminal desktop recovery was not dispatched.");
                return;
            }

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (IsDesktopShellRunning())
                {
                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            Log.Warn("Terminal desktop recovery was not verified before its deadline.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Terminal desktop recovery failed", ex);
        }
    }

    /// <summary>Gets whether Explorer's desktop shell, not merely a folder window, runs in this session.</summary>
    /// <remarks>
    ///     Explorer is a per-session shell singleton, so starting it while its taskbar
    ///     exists only opens a File Explorer window. WSGM's own tray host also creates a
    ///     Shell_TrayWnd, which is why the owner must be the canonical explorer.exe. Folder windows
    ///     have run in their own explorer.exe since Windows 10 1903, so a process count says nothing
    ///     about the desktop; every mode decision asks this instead.
    /// </remarks>
    public static bool IsDesktopShellRunning()
    {
        nint taskbar = 0;
        while ((taskbar = NativeMethods.FindWindowExW(0, taskbar, "Shell_TrayWnd", null)) != 0)
        {
            if (!IsCurrentSessionWindow(taskbar))
            {
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(taskbar, out var owner);
            if (string.Equals(NativeShellProcess.TryGetImagePath(owner), ExplorerPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsCurrentSessionWindow(nint window)
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
}
