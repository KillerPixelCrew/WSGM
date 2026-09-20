using System;
using System.IO;
using Microsoft.Win32;

namespace WSGM.Plugin.Artwork;

internal static class SteamInstallation
{
    internal static string? ExecutablePath
    {
        get
        {
            try
            {
                var path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                    ?.GetValue("SteamExe") as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
                }

                var install = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?.GetValue("InstallPath") as string;
                return string.IsNullOrWhiteSpace(install)
                    ? null
                    : Path.Combine(Path.GetFullPath(install), "steam.exe");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ArtworkLog.Warn($"Steam installation lookup failed: {ex.Message}");
                return null;
            }
        }
    }
}
