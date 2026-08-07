using System;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Builds the Steam launch-option command for WSGM's normal-integrity wrapper.</summary>
internal static class DeelevationCommand
{
    internal const string HelperFileName = "WSGM.Deelevate.exe";

    internal static string HelperPathForCurrentDeployment()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(directory ?? Installer.InstallDir, HelperFileName);
    }

    internal static string SteamLaunchOptions(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            throw new ArgumentException("A helper path is required.", nameof(helperPath));
        }
        return $"\"{helperPath}\" %command%";
    }

    internal static void StopRunningHelpers(string reason)
    {
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(HelperFileName)))
        {
            try
            {
                Log.Info($"Stopping de-elevation helper pid {process.Id} ({reason}).");
                // The medium child owns the launched game/emulator. Ending its
                // complete tree releases both the wrapper executable and target
                // before an update/uninstall replaces or removes the helper.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop de-elevation helper pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
