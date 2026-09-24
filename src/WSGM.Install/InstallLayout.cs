using System;
using System.IO;

namespace WSGM.Install;

/// <summary>Where an installed WSGM lives. Setup and WSGM read the same constants.</summary>
/// <remarks>
///     The product is administrator-protected under <c>%ProgramFiles%\WSGM</c>; per-user state stays in
///     <c>%LOCALAPPDATA%\WSGM</c> and is not described here. Machine-wide installation records sit in
///     <c>%ProgramData%\WSGM</c>, which also holds the logs.
/// </remarks>
public static class InstallLayout
{
    /// <summary>The product root, <c>%ProgramFiles%\WSGM</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">Windows reported no Program Files directory.</exception>
    public static string Root => Path.Combine(KnownFolder(Environment.SpecialFolder.ProgramFiles), "WSGM");

    /// <summary>The installed application: WSGM, its launchers, the logon service and their libraries.</summary>
    public static string App => Path.Combine(Root, "App");

    /// <summary>The installed WSGM executable.</summary>
    public static string AppExe => Path.Combine(App, "WSGM.exe");

    /// <summary>The folder of <c>.wsgmpkg</c> files, device and common alike.</summary>
    public static string Plugins => Path.Combine(Root, "Plugins");

    /// <summary>The installed copy of setup, which is also the uninstaller.</summary>
    public static string Setup => Path.Combine(Root, "Setup");

    /// <summary>The installed setup executable.</summary>
    public static string SetupExe => Path.Combine(Setup, "WSGM.Setup.exe");

    /// <summary>Every plugin the installed release bundles, for repair and the Plugins page.</summary>
    public static string SetupPackages => Path.Combine(Setup, "Packages");

    /// <summary>Machine-wide installation records and logs, <c>%ProgramData%\WSGM</c>.</summary>
    public static string MachineData => Path.Combine(KnownFolder(Environment.SpecialFolder.CommonApplicationData), "WSGM");

    /// <summary>The manifest of the bundle this install came from.</summary>
    public static string InstalledBundle => Path.Combine(MachineData, "bundle.json");

    /// <summary>The system components setup installed itself, and may therefore remove.</summary>
    public static string InstalledComponents => Path.Combine(MachineData, "components.json");

    /// <summary>Plugin files the Plugins page asked to remove once they are no longer loaded.</summary>
    public static string PendingPluginRemovals => Path.Combine(MachineData, "plugin-removals.json");

    private static string KnownFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path)
            ? throw new DirectoryNotFoundException($"Windows did not report the {folder} directory.")
            : path;
    }
}
