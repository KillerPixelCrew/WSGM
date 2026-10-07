using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Finds the existing USBip tool and exposes it once to this process.</summary>
internal static class UsbipTool
{
    private static int _exposed;

    /// <summary>Adds an installed USBip folder to this process PATH at most once, logging when unavailable.</summary>
    /// <remarks>Does not install or repair USBip and does not modify the user or machine environment.</remarks>
    internal static void ExposeOnce()
    {
        if (Interlocked.Exchange(ref _exposed, 1) != 0)
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var location = Resolve(path, ReadInstallEntries(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists);
        if (location.OnPath)
        {
            return;
        }

        if (location.Folder is not { } folder)
        {
            Log.Warn("usbip.exe was not found on PATH or in a usbip-win2 install folder; attach will fail.");
            return;
        }

        Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + path);
        Log.Info($"usbip.exe is not on PATH; using {folder} for this process.");
    }

    private static IEnumerable<UninstallEntry> ReadInstallEntries()
    {
        foreach (var entry in UninstallEntries.Read())
        {
            yield return entry;
        }
    }

    /// <summary>Locates USBip using PATH, installed-package metadata and the standard installation folder.</summary>
    /// <param name="path">Current process PATH.</param>
    /// <param name="entries">Installed-program metadata to inspect without mutation.</param>
    /// <param name="programFiles">Program Files root for the final USBip fallback.</param>
    /// <param name="fileExists">Filesystem existence check supplied by the caller.</param>
    /// <returns>A PATH hit, discovered folder, or a missing result with neither set.</returns>
    internal static UsbipLocation Resolve(string path, IEnumerable<UninstallEntry> entries,
        string programFiles, Func<string, bool> fileExists)
    {
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (fileExists(Path.Combine(entry.Trim(), "usbip.exe")))
            {
                return new UsbipLocation(true, null);
            }
        }

        foreach (var entry in entries)
        {
            if (entry.DisplayName.StartsWith("USBip", StringComparison.OrdinalIgnoreCase)
                && entry.InstallLocation.Length > 0)
            {
                var folder = entry.InstallLocation.TrimEnd('\\');
                if (fileExists(Path.Combine(folder, "usbip.exe")))
                {
                    return new UsbipLocation(false, folder);
                }
            }
        }

        var fallback = Path.Combine(programFiles, "USBip");
        return new UsbipLocation(false, fileExists(Path.Combine(fallback, "usbip.exe")) ? fallback : null);
    }
}

/// <summary>USBip discovery result; no executable is started.</summary>
/// <param name="OnPath">Whether usbip.exe already resolves from PATH.</param>
/// <param name="Folder">Directory to prepend when not on PATH; null if already found there or missing.</param>
internal readonly record struct UsbipLocation(bool OnPath, string? Folder);
