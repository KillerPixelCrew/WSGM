using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Windows registry, file-version and process observation for RTSS discovery.</summary>
internal sealed class WindowsRtssDiscoveryEnvironment : IRtssDiscoveryEnvironment
{
    // RTSS's own key carries InstallDir; its uninstall registration carries InstallLocation. The
    // installer is 32-bit, so the 32-bit view is read first.
    private static readonly (string Key, string Value)[] Locations =
    [
        (@"SOFTWARE\Unwinder\RTSS", "InstallDir"),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS", "InstallLocation")
    ];

    private static readonly RegistryView[] Views = [RegistryView.Registry32, RegistryView.Registry64];

    /// <inheritdoc />
    public string? ReadInstallLocation()
    {
        foreach (var (key, value) in Locations)
        {
            foreach (var view in Views)
            {
                try
                {
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var subKey = machine.OpenSubKey(key, false);
                    if (subKey?.GetValue(value) is string location && !string.IsNullOrWhiteSpace(location))
                    {
                        return location;
                    }
                }
                catch (Exception ex)
                {
                    Log.Change(
                        $"rtss.discovery.registration.{view}",
                        $"RTSS discovery could not read the {view} registration: {ex.Message}",
                        LogLevel.Warn);
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    /// <inheritdoc />
    public Version ReadProductVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new Version(version.ProductMajorPart, version.ProductMinorPart, version.ProductBuildPart);
    }

    /// <inheritdoc />
    public IReadOnlyList<RtssProcessIdentity> ReadProcesses()
    {
        List<RtssProcessIdentity> result = [];
        foreach (var process in Process.GetProcessesByName("RTSS"))
        {
            using (process)
            {
                try
                {
                    var path = NativeShellProcess.TryGetImagePath((uint)process.Id);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(new RtssProcessIdentity(process.Id, path, process.StartTime.ToUniversalTime()));
                    }
                }
                catch (Exception ex)
                {
                    Log.Change(
                        "rtss.discovery.process",
                        $"RTSS discovery could not inspect process {process.Id}: {ex.Message}",
                        LogLevel.Warn);
                }
            }
        }

        return result;
    }
}
