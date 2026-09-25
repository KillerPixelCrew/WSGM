using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>The logon service as setup saw it before touching it.</summary>
/// <param name="Exists">Whether the service is registered.</param>
/// <param name="Running">Whether it was running.</param>
internal readonly record struct ServiceState(bool Exists, bool Running);

/// <summary>How a running WSGM answered the exit request.</summary>
internal enum ShutdownHandoff
{
    /// <summary>No WSGM was listening.</summary>
    NotRunning,

    /// <summary>A build without the completion channel; the bounded fallback applies.</summary>
    Legacy,

    /// <summary>WSGM exited and confirmed its cleanup.</summary>
    Completed,

    /// <summary>WSGM did not finish in time; the force fallback applies.</summary>
    TimedOut,

    /// <summary>The request could not be delivered.</summary>
    Failed
}

/// <summary>
///     Windows operations ported from the Inno installer, with the same names, access rights, waits
///     and refusals. The exit events are a cross-version contract with every running WSGM.
/// </summary>
internal static class WindowsSetup
{
    internal const string ServiceName = "WSGMLogonService";
    internal const string ExitForUpdate = @"Local\WSGM.ExitForUpdate";
    internal const string ExitForUninstall = @"Local\WSGM.ExitForUninstall";
    internal const string ShellMutex = @"Local\WSGM.Shell";
    internal const string DeviceOwner = @"Global\WSGM.DeviceOwner";
    internal const string AnchorRecoverySettled = @"Local\WSGM.ShellAnchor.RecoverySettled";

    /// <summary>Mirrors Core\Steam.cs: HKCU SteamExe, then the machine-wide install path.</summary>
    public static bool SteamInstalled()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (user?.GetValue("SteamExe") is string exe && File.Exists(exe.Replace('/', '\\')))
        {
            return true;
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Valve\Steam");
        return machine?.GetValue("InstallPath") is string dir && File.Exists(Path.Combine(dir, "steam.exe"));
    }

    /// <summary>Reads the logon service state, or null when it is in a transitional state or unreadable.</summary>
    public static ServiceState? InspectService()
    {
        var manager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.ScManagerConnect);
        if (manager == 0)
        {
            SetupLog.Warn("Could not open the Service Control Manager to inspect " + ServiceName);
            return null;
        }

        try
        {
            var service = NativeMethods.OpenServiceW(manager, ServiceName, NativeMethods.ServiceQueryStatus);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorServiceDoesNotExist)
                {
                    return new ServiceState(false, false);
                }

                SetupLog.Warn($"Could not inspect {ServiceName}; error={error}");
                return null;
            }

            try
            {
                if (!NativeMethods.QueryServiceStatus(service, out var status))
                {
                    return null;
                }

                return status.dwCurrentState switch
                {
                    NativeMethods.ServiceStopped => new ServiceState(true, false),
                    NativeMethods.ServiceRunning => new ServiceState(true, true),
                    _ => null
                };
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    /// <summary>
    ///     Stops the logon service before WSGM: with the service alive, a stopped WSGM trips its watchdog,
    ///     which starts Explorer mid-update.
    /// </summary>
    /// <returns>Whether the service is now stopped or absent.</returns>
    public static bool StopService()
    {
        if (InspectService() is not { } state)
        {
            return false;
        }

        if (!state.Exists || !state.Running)
        {
            return true;
        }

        var code = Run(SystemTool("sc.exe"), $"stop {ServiceName}");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (InspectService() is { } now && (!now.Exists || !now.Running))
            {
                return true;
            }

            Thread.Sleep(250);
        }

        SetupLog.Warn($"{ServiceName} did not stop within ten seconds (sc exit {code}).");
        return false;
    }

    /// <summary>Whether a WSGM shell holds its session mutex.</summary>
    public static bool ShellRunning()
    {
        if (Mutex.TryOpenExisting(ShellMutex, out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Signals a running WSGM to exit through a named event and waits for it, exactly as the Inno
    ///     installer did: reset the optional <c>.Completed</c> event, set the request, then poll until the
    ///     shell mutex is gone and completion was published.
    /// </summary>
    /// <param name="eventName">The exit event.</param>
    /// <param name="graceIterations">Half-second polls before giving up.</param>
    /// <returns>How WSGM answered.</returns>
    public static ShutdownHandoff RequestExit(string eventName, int graceIterations)
    {
        EventWaitHandle.TryOpenExisting(eventName + ".Completed", out var completion);
        using (completion)
        {
            completion?.Reset();
            if (!EventWaitHandle.TryOpenExisting(eventName, out var request))
            {
                return ShutdownHandoff.NotRunning;
            }

            using (request)
            {
                if (!request.Set())
                {
                    return completion is null ? ShutdownHandoff.Legacy : ShutdownHandoff.Failed;
                }

                var completed = false;
                for (var i = 0; i < graceIterations; i++)
                {
                    completed |= completion?.WaitOne(0) == true;
                    if (!ShellRunning() && (completion is null || completed))
                    {
                        break;
                    }

                    Thread.Sleep(500);
                }

                Thread.Sleep(500);
                return completion is null
                    ? ShutdownHandoff.Legacy
                    : completed && !ShellRunning()
                        ? ShutdownHandoff.Completed
                        : ShutdownHandoff.TimedOut;
            }
        }
    }

    /// <summary>Stops WSGM for an update: the 44-poll budget covers WSGM's Steam pre-stop and cleanup.</summary>
    public static ShutdownHandoff StopForUpdate()
    {
        var handoff = RequestExit(ExitForUpdate, 44);
        SetupLog.Info($"Update shutdown handoff: {handoff}");
        ForceStopCurrentSession("WSGM.exe");
        WaitForShellAnchorRecovery();
        return handoff;
    }

    /// <summary>Stops WSGM for uninstall, falling back to the update event for older builds.</summary>
    public static ShutdownHandoff StopForUninstall()
    {
        var handoff = RequestExit(ExitForUninstall, 40);
        if (handoff is ShutdownHandoff.NotRunning)
        {
            handoff = RequestExit(ExitForUpdate, 44);
        }

        SetupLog.Info($"Uninstall shutdown handoff: {handoff}");
        ForceStopCurrentSession("WSGM.exe");
        WaitForShellAnchorRecovery();
        return handoff;
    }

    /// <summary>
    ///     Asks Steam to exit the way WSGM's own update pre-stop does (<c>steam://exit</c>) and waits for it.
    ///     Steam is never terminated: it may be saving a running game, so a Steam that stays is a refusal.
    /// </summary>
    /// <param name="budget">How long Steam may take to shut down.</param>
    /// <returns>Whether Steam is gone from this session.</returns>
    public static bool CloseSteam(TimeSpan budget)
    {
        if (!Blockers(true).Contains("steam", StringComparer.OrdinalIgnoreCase))
        {
            // Steam elsewhere is not ours to close, but saying so tells a session mismatch from no Steam at all.
            var elsewhere = Process.GetProcessesByName("steam");
            SetupLog.Info("Steam is not running in this session"
                          + (elsewhere.Length == 0
                              ? "."
                              : $"; found in session(s) {string.Join(", ", elsewhere.Select(Session).Distinct())}."));
            foreach (var process in elsewhere)
            {
                process.Dispose();
            }

            return true;
        }

        SetupLog.Info("Steam is still running; asking it to exit (steam://exit).");
        try
        {
            Process.Start(new ProcessStartInfo("steam://exit") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            SetupLog.Error("Could not ask Steam to exit", ex);
            return false;
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget)
        {
            Thread.Sleep(500);
            if (!Blockers(true).Contains("steam", StringComparer.OrdinalIgnoreCase))
            {
                SetupLog.Info($"Steam exited after {deadline.Elapsed.TotalSeconds:0.0} s.");
                return true;
            }
        }

        SetupLog.Warn($"Steam did not exit within {budget.TotalSeconds:0} s; it was not terminated.");
        return false;
    }

    /// <summary>The image path of the WSGM running in this session, so a rollback restarts that one.</summary>
    public static string? RunningWsgmPath()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("WSGM"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == self.SessionId && process.MainModule?.FileName is { } path)
                    {
                        return path;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Exited while enumerating, or not readable.
                }
            }
        }

        return null;
    }

    private static string Session(Process process)
    {
        try
        {
            return process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return "?";
        }
    }

    /// <summary>Force-stops an image in this session only; another signed-in user's WSGM is never touched.</summary>
    public static void ForceStopCurrentSession(string image)
    {
        if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
        {
            SetupLog.Warn("Could not resolve the setup session; refusing a cross-session force stop for " + image);
            return;
        }

        Run(SystemTool("taskkill.exe"), $"/FI \"SESSION eq {session}\" /IM \"{image}\" /F");
    }

    /// <summary>
    ///     Waits for the shell anchor to publish its recovery decision before retiring it; it may be the
    ///     only process able to restore Explorer.
    /// </summary>
    public static void WaitForShellAnchorRecovery()
    {
        if (!EventWaitHandle.TryOpenExisting(AnchorRecoverySettled, out var settled))
        {
            return;
        }

        using (settled)
        {
            if (settled.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Thread.Sleep(250);
                ForceStopCurrentSession("WSGM.ShellAnchor.exe");
            }
            else
            {
                SetupLog.Warn("Shell-anchor recovery acknowledgement timed out; the anchor stays alive.");
            }
        }
    }

    /// <summary>
    ///     Whether Steam or a launch wrapper runs in this session. Setup never terminates either: either
    ///     can own a running game that needs its normal save and exit.
    /// </summary>
    public static IReadOnlyList<string> Blockers(bool includeSteam)
    {
        // WSGM.Deelevate and steam-input-lease are the wrappers' retired names; a 1.0 install may still run them.
        string[] wrappers = ["WSGM.Launch", "WSGM.PackagedLaunch", "WSGM.Deelevate", "steam-input-lease"];
        var names = includeSteam ? ["steam", .. wrappers] : wrappers;
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        List<string> found = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == session
                        && names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(process.ProcessName);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Exited while enumerating.
                }
            }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    ///     Elects setup as the machine's hardware owner. WSGM and Device Lab hold the same unowned marker
    ///     while plugin code may run; "already exists" means one of them still does. A process that just
    ///     exited can hold it a moment longer, and the old Inno uninstaller hands off to a temporary copy that
    ///     releases it only as it finishes, so setup waits for the release instead of refusing at once.
    /// </summary>
    /// <param name="wait">How long another owner may take to let go.</param>
    /// <returns>The held marker, or null when another owner stayed active.</returns>
    public static Mutex? ReserveDeviceOwner(TimeSpan wait)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            Mutex marker = new(false, DeviceOwner, out var createdNew);
            if (createdNew)
            {
                return marker;
            }

            marker.Dispose();
            if (deadline.Elapsed >= wait)
            {
                SetupLog.Warn($"The device-owner marker was still held after {wait.TotalSeconds:0} s.");
                return null;
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>Runs a program hidden and returns its exit code, or -1 when it could not start.</summary>
    public static int Run(string file, string arguments, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                return -1;
            }

            if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
            {
                SetupLog.Warn($"{Path.GetFileName(file)} {arguments} did not finish in time.");
                return -1;
            }

            SetupLog.Info($"{Path.GetFileName(file)} {arguments} exited {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            SetupLog.Error($"Could not start {file}", ex);
            return -1;
        }
    }

    /// <summary>Starts a program without waiting, visibly, as the user would.</summary>
    public static void Start(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            SetupLog.Error($"Could not start {file}", ex);
        }
    }

    /// <summary>A tool in System32, never resolved through PATH.</summary>
    public static string SystemTool(string name)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
    }

    /// <summary>Writes a shortcut.</summary>
    /// <param name="path">The .lnk file.</param>
    /// <param name="target">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="description">The tooltip.</param>
    public static void CreateShortcut(string path, string target, string arguments, string description)
    {
        var interfaceId = typeof(NativeMethods.IShellLinkW).GUID;
        var result = NativeMethods.CoCreateInstance(NativeMethods.ShellLinkClsid, 0, 1, interfaceId, out var pointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            StrategyBasedComWrappers wrappers = new();
            var instance = wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            var link = (NativeMethods.IShellLinkW)instance;
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((NativeMethods.IPersistFile)instance).Save(path, true);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Deletes a file or directory now, or at the next reboot when it is in use.</summary>
    public static void DeleteOrScheduleAtReboot(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeMethods.MoveFileExW(path, null, NativeMethods.MoveFileDelayUntilReboot);
            SetupLog.Warn($"{path} is in use and is deleted at the next restart.");
        }
    }
}
