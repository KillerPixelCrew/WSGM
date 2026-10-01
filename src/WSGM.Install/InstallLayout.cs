using System;
using System.IO;
using System.Text.Json;

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
    public static string MachineData =>
        Path.Combine(KnownFolder(Environment.SpecialFolder.CommonApplicationData), "WSGM");

    /// <summary>The manifest of the bundle this install came from.</summary>
    public static string InstalledBundle => Path.Combine(MachineData, "bundle.json");

    /// <summary>Durable setup file-transaction record, shared with the sign-in guard.</summary>
    public static string SetupTransaction => Path.Combine(MachineData, "setup-transaction.json");

    /// <summary>Whether an incomplete or unreadable setup must prevent normal runtime startup.</summary>
    public static bool HasPendingSetup
    {
        get
        {
            if (!File.Exists(SetupTransaction))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(SetupTransaction, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16 * 1024)
                {
                    return true;
                }

                using var document = JsonDocument.Parse(stream);
                var record = document.RootElement;
                if (record.ValueKind != JsonValueKind.Object
                    || !record.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                    !schema.TryGetInt32(out var version) || version != 1)
                {
                    return true;
                }

                return !((record.TryGetProperty("Committed", out var committed) &&
                          committed.ValueKind == JsonValueKind.True)
                         || (record.TryGetProperty("RolledBack", out var restored) &&
                             restored.ValueKind == JsonValueKind.True));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return true;
            }
        }
    }

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
