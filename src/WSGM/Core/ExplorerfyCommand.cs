using System;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Builds the Steam launch-option command for WSGM's Explorer-companion
/// wrapper (games that need Windows Explorer running).</summary>
internal static class ExplorerfyCommand
{
    internal const string HelperFileName = "WSGM.Explorerfy.exe";

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
                Log.Info($"Stopping Explorer-companion helper pid {process.Id} ({reason}).");
                // The wrapper owns the launched game. Ending its complete tree
                // releases both the wrapper and target before an update/uninstall
                // replaces or removes the helper.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop Explorer-companion helper pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
