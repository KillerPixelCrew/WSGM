using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     AMD Software: Adrenalin Edition's background host as seen from this session. Authors of other
///     ADLX-based tools report that AMD's two service-wide settings, AFMF and RSR, only take effect while
///     it runs. Windows starts it from the "StartCN" Run entry, which Explorer does not reach before Game
///     Mode retires it, so a refused AFMF or RSR write is logged next to whether the host is here at all.
/// </summary>
public static class AmdSoftwareHost
{
    /// <summary>The process names, without extension, AMD Software runs in the user's session.</summary>
    internal static readonly string[] ProcessNames = ["RadeonSoftware", "AMDRSServ", "cncmd"];

    /// <summary>The launcher Windows' own Run entry starts the host with, as <c>cncmd.exe restart</c>.</summary>
    internal static string LauncherPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "CNext", "CNext", "cncmd.exe");

    /// <summary>Whether a graphics capability is one ADLX exposes once for every GPU rather than per adapter.</summary>
    /// <param name="capabilityId">The capability identifier without its instance.</param>
    /// <returns><see langword="true" /> for AFMF, RSR and their options.</returns>
    internal static bool IsServiceWide(string capabilityId)
    {
        return capabilityId is "graphics.afmf" or "graphics.rsr"
               || capabilityId.StartsWith("graphics.afmf-", StringComparison.Ordinal)
               || capabilityId.StartsWith("graphics.rsr-", StringComparison.Ordinal);
    }

    /// <summary>Describes the live host processes and launcher for the log.</summary>
    /// <returns>One line naming each process as running or absent and whether the launcher is installed.</returns>
    internal static string Describe()
    {
        return Describe(IsRunning, File.Exists(LauncherPath));
    }

    /// <summary>Describes the host from supplied facts.</summary>
    /// <param name="isRunning">Whether a process of the given name runs in this session.</param>
    /// <param name="launcherPresent">Whether the launcher exists on disk.</param>
    /// <returns>One line naming each process as running or absent and whether the launcher is installed.</returns>
    internal static string Describe(Func<string, bool> isRunning, bool launcherPresent)
    {
        var processes = string.Join(", ",
            ProcessNames.Select(name => $"{name}.exe {(isRunning(name) ? "running" : "absent")}"));
        return $"AMD Software host: {processes}; launcher {(launcherPresent ? "present" : "absent")} at {LauncherPath}.";
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
