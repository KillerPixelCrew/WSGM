using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Self-installer: WSGM.exe is its own setup. Everything is per-user —
/// no elevation, no MSI/MSIX. Installs to %LOCALAPPDATA%\WSGM\bin, adds a Start
/// Menu shortcut and an entry in Settings → Apps; uninstall reverses all of it
/// (including the shell registration, if active).</summary>
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WSGM";

    public static string InstallDir => Path.Combine(Log.Directory, "bin");
    public static string InstalledExePath => Path.Combine(InstallDir, "WSGM.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WSGM.lnk");

    public static bool IsRunningFromInstallDir =>
        string.Equals(Environment.ProcessPath, InstalledExePath, StringComparison.OrdinalIgnoreCase);

    public static bool IsAppInstalled => File.Exists(InstalledExePath);

    /// <summary>True when the install was made by the Inno Setup installer, which then
    /// owns the shortcut and the Settings → Apps entry.</summary>
    public static bool IsInnoManaged => File.Exists(Path.Combine(InstallDir, "unins000.exe"));

    /// <summary>Copies the running exe into the install dir, creates the Start Menu
    /// shortcut and the Apps uninstall entry. Returns the installed exe path.</summary>
    public static string InstallApp()
    {
        Directory.CreateDirectory(InstallDir);

        // Clean up leftovers from a previous update-while-running swap.
        foreach (var old in Directory.GetFiles(InstallDir, "*.old"))
        {
            try { File.Delete(old); } catch { }
        }

        if (!IsRunningFromInstallDir)
        {
            var source = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine own executable path");
            var sourceDir = Path.GetDirectoryName(source)!;

            // NativeAOT keeps Skia/ANGLE as native sibling DLLs — the exe alone is
            // not a complete install. Copy the whole payload (exe + dlls, no pdb).
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var target = Path.Combine(InstallDir, Path.GetFileName(file));
                try
                {
                    File.Copy(file, target, overwrite: true);
                }
                catch (IOException)
                {
                    // Target is loaded by a running instance (e.g. the active shell).
                    // A loaded file can't be overwritten but CAN be renamed — swap it.
                    File.Move(target, target + ".old", overwrite: true);
                    File.Copy(file, target, overwrite: true);
                    Log.Info($"Swapped in-use file via rename: {Path.GetFileName(file)}");
                }
            }
        }

        if (!IsInnoManaged)
        {
            CreateShortcut();
            RegisterUninstallEntry();
        }
        Log.Info($"Installed to {InstalledExePath}");
        return InstalledExePath;
    }

    /// <summary>Removes shell registration (if ours), shortcut, uninstall entry, and
    /// finally the whole %LOCALAPPDATA%\WSGM directory via a detached delayed
    /// delete (an exe cannot delete itself while running).</summary>
    public static void UninstallApp()
    {
        ShellRegistration.Uninstall();

        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        Log.Info("Uninstalling — scheduling directory removal.");
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{Log.Directory}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to schedule directory removal", ex);
        }
    }

    private static void CreateShortcut()
    {
        // .lnk creation needs IShellLink (COM). Spawning Windows PowerShell for this
        // one-shot task avoids in-process COM interop under NativeAOT.
        var script =
            "$ws = New-Object -ComObject WScript.Shell; " +
            $"$s = $ws.CreateShortcut('{ShortcutPath}'); " +
            $"$s.TargetPath = '{InstalledExePath}'; " +
            $"$s.WorkingDirectory = '{InstallDir}'; " +
            "$s.Description = 'WSGM settings'; " +
            "$s.Save()";
        if (!ConsoleTool.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\""))
        {
            Log.Warn("Could not create Start Menu shortcut.");
        }
    }

    private static void RegisterUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        key.SetValue("DisplayName", "WSGM");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "WSGM");
        key.SetValue("DisplayIcon", InstalledExePath);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExePath}\" --uninstall-app");
        key.SetValue("QuietUninstallString", $"\"{InstalledExePath}\" --uninstall-app");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            var sizeKb = (int)(new FileInfo(InstalledExePath).Length / 1024);
            key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
        }
        catch { }
    }
}
