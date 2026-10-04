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
            await using var host = new ExplorerDesktopHost(context);
            var result = await host.RestoreDesktopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (result.Outcome == ExplorerDesktopOutcome.Failed)
            {
                Log.Warn($"Terminal desktop recovery was not verified: {result.Detail}.");
            }
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
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (!IsCurrentSessionWindow(taskbar))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(taskbar, out var owner);
        try
        {
            return string.Equals(NativeShellProcess.TryGetImagePath(owner), ExplorerPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or Win32Exception or OverflowException)
        {
            return false;
        }
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
