using System;
using System.IO;
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

    /// <summary>Big Picture window class (paired with the steamwebhelper process —
    /// SDL_app alone is not unique to Steam).</summary>
    public const string BigPictureWindowClass = "SDL_app";

    public const string OpenBigPictureUrl = "steam://open/bigpicture";
    public const string CloseBigPictureUrl = "steam://close/bigpicture";
    /// <summary>Graceful full Steam shutdown (verified client URL).</summary>
    public const string ExitUrl = "steam://exit";

    /// <summary>Full path to steam.exe from the registry, or null when Steam is not
    /// installed. HKCU value uses forward slashes — normalized here.</summary>
    public static string? ExePath
    {
        get
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
    }

    public static bool IsInstalled => ExePath is not null;

    public static bool IsRunning => WindowFinder.FindProcessIds(ProcessNames).Count > 0;

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
}
