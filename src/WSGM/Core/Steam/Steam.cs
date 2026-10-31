using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamUiToolkit;
using WSGM.Interop;
using WSGM.Shared;

namespace WSGM.Core;

/// <summary>
///     Everything WSGM knows about Steam. WSGM is Steam-exclusive: Steam is
///     located via the registry (no path configuration), started/focused/closed via
///     steam:// protocol URLs (UIPI-proof, and the handler boots Steam when needed),
///     and its Big Picture window is recognized by class+process.
/// </summary>
public static class Steam
{
    /// <summary>steam.exe plus the process that owns the Big Picture window.</summary>
    public const string ProcessNames = "steam;steamwebhelper";

    /// <summary>
    ///     Big Picture window class (paired with the steamwebhelper process —
    ///     SDL_app alone is not unique to Steam).
    /// </summary>
    private const string BigPictureWindowClass = "SDL_app";

    /// <summary>Protocol URL that opens Steam Big Picture mode.</summary>
    public const string OpenBigPictureUrl = "steam://open/bigpicture";

    /// <summary>Protocol URL that exits Steam Big Picture mode.</summary>
    public const string CloseBigPictureUrl = "steam://close/bigpicture";

    private static readonly TimeSpan UpdateGracefulExitBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Gets the complete bounded pre-shutdown window used to release Steam and launch
    ///     wrappers before WSGM starts its separate application-cleanup deadline.
    /// </summary>
    internal static TimeSpan UpdateStopBudget => TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Full path to steam.exe from the registry, or null when Steam is not
    ///     installed. HKCU value uses forward slashes — normalized here. The registry+disk
    ///     probe runs once; later reads only re-validate the cached path with File.Exists
    ///     and re-probe when it went missing (uninstall/move).
    /// </summary>
    public static string? ExePath
    {
        get
        {
            var cached = field;
            if (cached is not null && File.Exists(cached))
            {
                return cached;
            }

            field = ResolveExePath();
            return field;
        }
    }

    /// <summary>
    ///     Gets Steam's install directory, or <see langword="null" /> when Steam
    ///     is not installed. Everything WSGM writes beside Steam - the CEF debug flag and
    ///     the Steam Input proxy - resolves through here rather than repeating the
    ///     directory split at each call site.
    /// </summary>
    public static string? InstallDirectory =>
        ExePath is { } exe ? Path.GetDirectoryName(exe) : null;

    /// <summary>
    ///     Gets the full path of Steam's <c>config\libraryfolders.vdf</c> —
    ///     the install-folder registry every card/library feature reads and edits —
    ///     or <see langword="null" /> when Steam is not installed.
    /// </summary>
    private static string? LibraryFoldersConfigPath =>
        InstallDirectory is { } directory
            ? Path.Combine(directory, "config", "libraryfolders.vdf")
            : null;

    /// <summary>Gets whether a usable Steam executable was found.</summary>
    public static bool IsInstalled => ExePath is not null;

    /// <summary>Gets whether a Steam client or Big Picture helper process is running.</summary>
    public static bool IsRunning => ProcessLookup.FindProcessIds(ProcessNames).Count > 0;

    /// <summary>
    ///     Gets whether Steam's process-owned Big Picture window exists. This is
    ///     deliberately stronger than <see cref="IsRunning" />: on a cold start Steam's
    ///     process and headless CEF context exist before its UI is safe for autonomous
    ///     mutation.
    /// </summary>
    public static bool IsBigPictureVisible => FindBigPictureWindow() != IntPtr.Zero;

    /// <summary>
    ///     Gets whether WSGM must match Steam's elevated integrity level so
    ///     raw-touch gestures and overlay input are not blocked by UIPI.
    /// </summary>
    public static bool RequiresElevatedShell
    {
        get
        {
            if (ProcessLookup.FindProcessIds(ProcessNames)
                .Any(processId => ElevationCheck.IsProcessElevated(processId) == true))
            {
                return true;
            }

            var path = ExePath;
            return path is not null && HasRunAsAdminCompatibilityLayer(path);
        }
    }

    /// <summary>Logs launch wrappers still running in this session so setup defers replacement.</summary>
    /// <param name="reason">Why the active helpers are being reported.</param>
    private static void LogActiveLaunchHelpers(string reason)
    {
        var currentSession = ProcessLookup.CurrentSessionId;
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(LaunchWrapperCommand.HelperFileName)))
        {
            try
            {
                if (process.SessionId != currentSession)
                {
                    continue;
                }

                Log.Warn(
                    $"Launch wrapper pid {process.Id} is still active ({reason}); setup must "
                    + "defer replacement until its game exits.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not inspect launch wrapper pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    ///     Finds Steam's process-owned Big Picture window, or zero when there is none. The
    ///     desktop return needs the handle itself, not just its existence: a Big Picture window that
    ///     outlives the close request is what the restored desktop has to come up underneath.
    /// </summary>
    /// <returns>The window handle, or <see cref="IntPtr.Zero" />.</returns>
    public static nint FindBigPictureWindow()
    {
        return WindowFinder.FindWindow(ProcessNames, BigPictureWindowClass);
    }

    /// <summary>Finds Steam's Big Picture window among processes the caller already enumerated.</summary>
    /// <param name="steamProcessIds">This session's steam and steamwebhelper process ids.</param>
    /// <returns>The window handle, or <see cref="IntPtr.Zero" />.</returns>
    internal static nint FindBigPictureWindow(HashSet<uint> steamProcessIds)
    {
        return WindowFinder.FindWindow(steamProcessIds, BigPictureWindowClass);
    }

    private static string? ResolveExePath()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) is string
                {
                    Length: > 0
                } exe)
            {
                exe = exe.Replace('/', '\\');
                if (File.Exists(exe))
                {
                    return exe;
                }
            }

            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string
                {
                    Length: > 0
                } dir)
            {
                var fromInstallDir = Path.Combine(dir, "steam.exe");
                if (File.Exists(fromInstallDir))
                {
                    return fromInstallDir;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam registry lookup failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    ///     Reads <c>config\libraryfolders.vdf</c>. False when Steam is not
    ///     installed or the file does not exist yet; <paramref name="path" /> still
    ///     carries the resolved location when only the file is missing, so a caller
    ///     can create it. Deliberately does NOT catch IO failures — the callers'
    ///     policies for an unreadable config differ.
    /// </summary>
    /// <param name="path">The config path, or null when Steam is not installed.</param>
    /// <param name="text">The file text, or null when it could not be resolved.</param>
    /// <returns>True when text was read; false for absent Steam or missing file. Filesystem read failures propagate.</returns>
    public static bool TryReadLibraryFolders(out string? path, out string? text)
    {
        path = LibraryFoldersConfigPath;
        text = path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        return text is not null;
    }

    private static bool CompatibilityLayerRequiresElevation(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            return false;
        }

        return layer.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.TrimStart('~', '!', '#').Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRunAsAdminCompatibilityLayer(string executablePath)
    {
        const string layersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        try
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(layersKey);
                    if (CompatibilityLayerRequiresElevation(key?.GetValue(executablePath) as string))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam compatibility-layer lookup failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    ///     Starts or focuses Big Picture the smooth way. Cold start passes the
    ///     BP URL as a command-line ARGUMENT to steam.exe so Steam boots straight into
    ///     Big Picture — fired as a protocol instead, the handler first brings Steam up
    ///     in desktop mode and only switches after login (user-reported wonkiness).
    ///     When Steam already runs, the protocol re-activates/enters BP (UIPI-proof).
    /// </summary>
    /// <param name="unelevated">Whether to request a de-elevated launch.</param>
    /// <param name="cefEnabled">Whether to enable remote debugging before a cold start.</param>
    /// <param name="context">The owner's task and diagnostic directory.</param>
    /// <param name="shim">The process's Steam Input shim, reconciled before a cold start.</param>
    /// <param name="steamInputManagement">Whether Steam Input Management is on in the current configuration.</param>
    /// <returns>Activation outcome, with caller-owned process wrapper when available; readiness must be observed separately.</returns>
    internal static LaunchResult LaunchBigPicture(UserDataContext context, SteamInputShim shim,
        bool steamInputManagement, bool unelevated = false, bool cefEnabled = true)
    {
        // The debug port flag only takes effect on a fresh Steam start, so it is written before
        // either path; WSGM finds Steam and passes the directory to the toolkit.
        SteamCef.EnsureRemoteDebuggingEnabled(InstallDirectory, cefEnabled);
        if (!IsRunning && ExePath is { } exe)
        {
            return ColdStart(context, shim, steamInputManagement, exe, OpenBigPictureUrl, unelevated);
        }

        return AppLauncher.StartProtocol(OpenBigPictureUrl);
    }

    /// <summary>
    ///     Starts the Steam client without Big Picture, for a desktop session. WSGM owning the
    ///     client start is what gives Steam WSGM's integrity, so Steam Input and the Steam Overlay
    ///     still reach elevated windows once the user's own Steam autostart is out of the way.
    /// </summary>
    /// <param name="unelevated">Whether to request a de-elevated launch.</param>
    /// <param name="cefEnabled">Whether to enable remote debugging before the start.</param>
    /// <returns>The launch result, or a started result when Steam already runs.</returns>
    /// <param name="context">The owner's task and diagnostic directory.</param>
    /// <param name="shim">The process's Steam Input shim, reconciled before a cold start.</param>
    /// <param name="steamInputManagement">Whether Steam Input Management is on in the current configuration.</param>
    internal static LaunchResult LaunchDesktop(UserDataContext context, SteamInputShim shim,
        bool steamInputManagement, bool unelevated = false, bool cefEnabled = true)
    {
        SteamCef.EnsureRemoteDebuggingEnabled(InstallDirectory, cefEnabled);
        if (IsRunning)
        {
            return new LaunchResult(null, true, false);
        }

        return ExePath is not { } exe
            ? new LaunchResult(null, false, false)
            : ColdStart(context, shim, steamInputManagement, exe, "", unelevated);
    }

    /// <summary>
    ///     The one cold Steam start: shim reconcile, integrity choice and the startup-trace hint.
    ///     Callers differ only in the arguments Steam is started with, and have already enabled the
    ///     debug port.
    /// </summary>
    private static LaunchResult ColdStart(UserDataContext context, SteamInputShim shim,
        bool steamInputManagement, string exe, string arguments, bool unelevated)
    {
        return ColdStart(steamInputManagement, shim.Reconcile,
            status => LaunchColdClient(context, status, exe, arguments, unelevated));
    }

    /// <summary>Orders the shim reconcile before either process-launch path.</summary>
    /// <param name="steamInputManagement">Desired Steam Input shim ownership for this cold start.</param>
    /// <param name="reconcile">Reconciles the shim before any launch can map it.</param>
    /// <param name="launch">Launches Steam using the reconciled shim status.</param>
    /// <returns>The launch delegate's activation result; a reconcile failure propagates.</returns>
    /// <remarks>The caller must establish Steam is not running.</remarks>
    internal static LaunchResult ColdStart(
        bool steamInputManagement,
        Func<bool, string, SteamInputShimStatus> reconcile,
        Func<SteamInputShimStatus, LaunchResult> launch)
    {
        // Steam is provably not running on this branch, which makes it the one
        // moment in a session when a stale Steam Input shim can actually be
        // replaced - anywhere else the image is mapped and the copy fails.
        return launch(reconcile(steamInputManagement, "steam-cold-start"));
    }

    private static LaunchResult LaunchColdClient(UserDataContext context,
        SteamInputShimStatus shimStatus, string exe, string arguments, bool unelevated)
    {
        // The de-elevating scheduled task is only meaningful from an elevated WSGM: started
        // from a medium-integrity process, the ordinary launch already produces a
        // medium-integrity Steam without the task-scheduler round trip.
        var deElevate = unelevated && ElevationCheck.IsCurrentProcessElevated() is true;
        if (deElevate)
        {
            var disposition = UnelevatedLauncher.TryStartViaScheduledTask(context, exe, arguments);
            if (disposition == ScheduledTaskLaunchDisposition.Dispatched)
            {
                Log.Info("Steam launch integrity: medium (de-elevated scheduled task).");
                return new LaunchResult(null, true, false);
            }

            if (disposition == ScheduledTaskLaunchDisposition.Unknown)
            {
                Log.Warn(
                    "Steam scheduled launch outcome is unknown; waiting for Steam detection without another launch.");
                return new LaunchResult(null, false, false);
            }

            Log.Warn("Steam launch integrity: de-elevation was not dispatched; "
                     + "falling back to WSGM's own integrity.");
        }

        var result = AppLauncher.Start(exe, arguments, false);
        Log.Info(
            "Steam launch integrity: "
            + (ElevationCheck.IsCurrentProcessElevated() is true ? "elevated" : "medium")
            + " (matched to WSGM).");
        // Only when a vector is actually deployed, and worded as the EXPECTED
        // path. docs\steam-input.md tells the reader a missing file means the
        // gate worker never got past the loader — so naming a path for a Steam
        // with no shim, or for a pid Steam's bootstrapper then re-execs away
        // from, makes that diagnostic assert the opposite of the truth.
        if (result.Process is { } process
            && shimStatus.State == SteamInputShimState.Deployed)
        {
            Log.Info(
                $"Steam Input shim startup trace expected for pid {process.Id}: "
                + SteamInputShim.StartupTracePath(context, process.Id)
                + " (absent if Steam re-execed into another pid)");
        }

        return result;
    }

    /// <summary>
    ///     Sends one of Steam's own Big Picture keyboard shortcuts globally.
    ///     This deliberately has no foreground-window gate: when a game is foreground,
    ///     Steam uses the shortcut to bring its menu up over that game.
    /// </summary>
    /// <param name="shortcut">The Big Picture menu shortcut to send.</param>
    /// <returns>True when Windows accepted the complete synthetic key chord.</returns>
    public static bool TrySendBigPictureShortcut(BigPictureShortcut shortcut)
    {
        var virtualKey = ShortcutVirtualKey(shortcut);
        var result = KeyboardInput.SendControlChord(virtualKey);
        if (result.Sent != result.Requested)
        {
            Log.Warn(
                $"Steam Big Picture shortcut Ctrl+{(char)virtualKey} failed " +
                $"(sent {result.Sent}/{result.Requested}, Win32 error {result.Error}).");
            return false;
        }

        // SendInput has no window target: Windows delivers the chord to whatever holds
        // focus. Naming that window is the only way a pasted log distinguishes "Steam
        // ignored it" from "it went somewhere else entirely".
        Log.Info($"Steam Big Picture shortcut Ctrl+{(char)virtualKey} sent ({shortcut}) "
                 + $"to foreground {WindowFinder.DescribeForeground()}.");
        return true;
    }

    /// <summary>Returns Steam's installed-client keyboard mapping for a Big Picture menu.</summary>
    /// <param name="shortcut">The menu shortcut to map.</param>
    /// <returns>The Win32 virtual key combined with Control.</returns>
    internal static ushort ShortcutVirtualKey(BigPictureShortcut shortcut)
    {
        return shortcut switch
        {
            BigPictureShortcut.SteamMenu => 0x31,
            BigPictureShortcut.QuickAccess => 0x32,
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
        };
    }

    /// <summary>Stops Steam within a bounded graceful and forced shutdown window.</summary>
    /// <param name="cancellationToken">Cancels waiting and prevents later forced termination.</param>
    /// <param name="entireProcessTree">Whether an explicit full-close request also ends Steam's children.</param>
    /// <returns>True only after this session's Steam client and CEF helpers have exited.</returns>
    internal static Task<bool> StopAsync(CancellationToken cancellationToken, bool entireProcessTree = false)
    {
        return StopAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15), cancellationToken, entireProcessTree);
    }

    /// <summary>Stops Steam using the caller's remaining graceful and forced verification budgets.</summary>
    /// <param name="budget">Total stop budget.</param>
    /// <param name="gracefulBudget">Time allowed for an orderly exit before forced termination.</param>
    /// <param name="cancellationToken">Cancels waiting and prevents later forced termination.</param>
    /// <param name="entireProcessTree">Whether the request also ends Steam's child processes.</param>
    /// <returns>True only after Steam and its CEF helpers have exited.</returns>
    internal static Task<bool> StopAsync(TimeSpan budget, TimeSpan gracefulBudget, CancellationToken cancellationToken,
        bool entireProcessTree = false)
    {
        return SteamProcessControl.StopAsync(budget, gracefulBudget,
            message => Log.Info(message), cancellationToken, entireProcessTree);
    }

    /// <summary>Stops Steam for an authorized application update, forcing exit after its grace.</summary>
    public static void StopForUpdate()
    {
        var stopped = SteamProcessControl.StopAsync(UpdateStopBudget, UpdateGracefulExitBudget,
            message => Log.Info(message), CancellationToken.None, entireProcessTree: true).GetAwaiter().GetResult();
        if (!stopped)
        {
            Log.Warn("Steam remained active after the update-stop budget; setup will enforce termination before replacement.");
        }

        LogActiveLaunchHelpers("update");
    }
}
