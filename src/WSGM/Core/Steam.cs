using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Everything WSGM knows about Steam. WSGM is Steam-exclusive: Steam is
/// located via the registry (no path configuration), started/focused/closed via
/// steam:// protocol URLs (UIPI-proof, and the handler boots Steam when needed),
/// and its Big Picture window is recognized by class+process.</summary>
public static class Steam
{
    /// <summary>steam.exe plus the process that owns the Big Picture window.</summary>
    public const string ProcessNames = "steam;steamwebhelper";

    /// <summary>Just steam.exe — deliberately narrower than <see cref="ProcessNames"/>:
    /// only the main client services steam:// protocol URLs, so a lingering
    /// steamwebhelper must not count as "Steam is running" for protocol callers.</summary>
    public const string MainProcessName = "steam";

    /// <summary>Big Picture window class (paired with the steamwebhelper process —
    /// SDL_app alone is not unique to Steam).</summary>
    public const string BigPictureWindowClass = "SDL_app";

    /// <summary>Protocol URL that opens Steam Big Picture mode.</summary>
    public const string OpenBigPictureUrl = "steam://open/bigpicture";

    /// <summary>Protocol URL that exits Steam Big Picture mode.</summary>
    public const string CloseBigPictureUrl = "steam://close/bigpicture";
    /// <summary>Graceful full Steam shutdown (verified client URL).</summary>
    public const string ExitUrl = "steam://exit";

    private static string? _cachedExePath;

    /// <summary>Full path to steam.exe from the registry, or null when Steam is not
    /// installed. HKCU value uses forward slashes — normalized here. The registry+disk
    /// probe runs once; later reads only re-validate the cached path with File.Exists
    /// and re-probe when it went missing (uninstall/move).</summary>
    public static string? ExePath
    {
        get
        {
            var cached = _cachedExePath;
            if (cached is not null && File.Exists(cached))
            {
                return cached;
            }
            _cachedExePath = ResolveExePath();
            return _cachedExePath;
        }
    }

    private static string? ResolveExePath()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) is string exe
                && exe.Length > 0)
            {
                exe = exe.Replace('/', '\\');
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string dir
                && dir.Length > 0)
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

    /// <summary>Gets whether a usable Steam executable was found.</summary>
    public static bool IsInstalled => ExePath is not null;

    /// <summary>Gets whether a Steam client or Big Picture helper process is running.</summary>
    public static bool IsRunning => WindowFinder.FindProcessIds(ProcessNames).Count > 0;

    /// <summary>Gets whether WSGM must match Steam's elevated integrity level so
    /// raw-touch gestures and overlay input are not blocked by UIPI.</summary>
    public static bool RequiresElevatedShell
    {
        get
        {
            foreach (var processId in WindowFinder.FindProcessIds(ProcessNames))
            {
                if (ElevationCheck.IsProcessElevated((uint)processId) == true)
                {
                    return true;
                }
            }

            var path = ExePath;
            return path is not null && HasRunAsAdminCompatibilityLayer(path);
        }
    }

    private static bool CompatibilityLayerRequiresElevation(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            return false;
        }
        foreach (var token in layer.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.TrimStart('~', '!', '#').Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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

    /// <summary>Starts or focuses Big Picture the smooth way. Cold start passes the
    /// BP URL as a command-line ARGUMENT to steam.exe so Steam boots straight into
    /// Big Picture — fired as a protocol instead, the handler first brings Steam up
    /// in desktop mode and only switches after login (user-reported wonkiness).
    /// When Steam already runs, the protocol re-activates/enters BP (UIPI-proof).</summary>
    public static AppLauncher.LaunchResult LaunchBigPicture()
    {
        if (!IsRunning && ExePath is { } exe)
        {
            return AppLauncher.Start(exe, OpenBigPictureUrl, elevated: false);
        }
        return AppLauncher.StartProtocol(OpenBigPictureUrl);
    }

    /// <summary>Stops Steam for an application update that must replace an
    /// injected payload DLL. First requests Steam's normal shutdown, then uses
    /// WSGM's possibly elevated token to end any client that remains after a
    /// bounded grace period; the unelevated installer cannot do this reliably.</summary>
    public static void StopForUpdate()
    {
        if (IsRunning)
        {
            Log.Info("Update requested — closing Steam to release the Steam Input payload.");
            AppLauncher.StartProtocol(ExitUrl);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var remaining = Process.GetProcessesByName(MainProcessName);
                if (remaining.Length == 0)
                {
                    Log.Info("Steam exited gracefully for update.");
                    break;
                }
                foreach (var process in remaining)
                {
                    process.Dispose();
                }
                Thread.Sleep(250);
            }

            foreach (var process in Process.GetProcessesByName(MainProcessName))
            {
                try
                {
                    Log.Warn($"Steam did not exit for update — ending process {process.Id}.");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not end Steam process {process.Id} for update: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        DeelevationCommand.StopRunningHelpers("update");
    }
}
