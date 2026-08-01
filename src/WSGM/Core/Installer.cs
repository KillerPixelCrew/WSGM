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

    /// <summary>Gets the stable per-user directory that holds the installed application files.</summary>
    public static string InstallDir => Path.Combine(Log.Directory, "bin");

    /// <summary>Gets the installed WSGM executable path.</summary>
    public static string InstalledExePath => Path.Combine(InstallDir, "WSGM.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WSGM.lnk");

    /// <summary>Gets whether the current process is the installed copy rather than a portable copy.</summary>
    public static bool IsRunningFromInstallDir =>
        string.Equals(Environment.ProcessPath, InstalledExePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether an installed WSGM executable exists.</summary>
    public static bool IsAppInstalled => File.Exists(InstalledExePath);

    /// <summary>True when the install was made by the Inno Setup installer, which then
    /// owns the shortcut and the Settings → Apps entry.</summary>
    public static bool IsInnoManaged => File.Exists(Path.Combine(InstallDir, "unins000.exe"));

    /// <summary>Copies the running exe into the install dir, creates the Start Menu
    /// shortcut and the Apps uninstall entry. Returns the installed exe path.</summary>
    public static string InstallApp()
    {
        Directory.CreateDirectory(InstallDir);

        // Clean up leftovers from a previous update-while-running swap
        // (both the fixed .old name and the unique .old-<n> fallbacks).
        foreach (var old in Directory.GetFiles(InstallDir, "*.old*"))
        {
            try { File.Delete(old); } catch { }
        }

        if (!IsRunningFromInstallDir)
        {
            var source = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine own executable path");
            var sourceDir = Path.GetDirectoryName(source)!;
            var sourceExeName = Path.GetFileName(source);

            // NativeAOT keeps Skia/ANGLE as native sibling DLLs — the exe alone is
            // not a complete install. Copy our own exe plus the runtime DLLs, but
            // nothing else: a portable run from a mixed folder (Downloads with other
            // setups/tools) must not sweep unrelated exes into the install dir.
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                var isDll = ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
                var isOwnExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(file).Equals(sourceExeName, StringComparison.OrdinalIgnoreCase);
                if (!isDll && !isOwnExe)
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
                    MoveAsideLockedTarget(target);
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

    /// <summary>Renames a loaded target file out of the way. The fixed .old name can
    /// itself still be mapped by an even older instance (two updates while the
    /// original process keeps running), so fall back to a unique .old-&lt;n&gt; name
    /// instead of letting the move throw out of InstallApp.</summary>
    private static void MoveAsideLockedTarget(string target)
    {
        try
        {
            File.Move(target, target + ".old", overwrite: true);
            return;
        }
        catch (IOException)
        {
            // .old is locked too — pick a fresh name below.
        }
        for (var n = 1; ; n++)
        {
            var aside = $"{target}.old-{n}";
            if (File.Exists(aside))
            {
                continue;
            }
            File.Move(target, aside);
            Log.Info($"Locked .old target, swapped aside as {Path.GetFileName(aside)}");
            return;
        }
    }

    /// <summary>Removes shell registration (if ours), shortcut, uninstall entry, and
    /// finally the whole %LOCALAPPDATA%\WSGM directory via a detached delayed
    /// delete (an exe cannot delete itself while running).</summary>
    public static void UninstallApp()
    {
        // A crashed pinned shell leaves the forced layout inside Steam, which
        // survives WSGM's removal — release unconditionally like every other
        // teardown/recovery path (invariant: fresh process can't know it pinned).
        SteamInputPin.ReleaseBestEffort("uninstall-app");

        ShellRegistration.Uninstall();

        // Roll back machine/user settings BEFORE the config directory (and the
        // snapshots inside config.json) are scheduled for deletion.
        RestoreMachineSettings();

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
                // cmd must not inherit a CWD inside the tree being deleted —
                // rmdir cannot remove a directory that is some process's CWD.
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to schedule directory removal", ex);
        }
    }

    /// <summary>Best-effort rollback of every machine/user setting WSGM changed
    /// outside its own directory: display scaling, UAC prompt level, lock-on-wake,
    /// and slate-mode posture. Called by UninstallApp and by --uninstall-restore;
    /// each step is isolated so one failure cannot stop the rest. The UAC and
    /// lock-on-wake writes need elevation (HKLM) — when this runs unelevated and
    /// either snapshot needs restoring, the whole restore is handed to one
    /// elevated --uninstall-restore instance (a single UAC prompt); declining
    /// leaves those two settings as-is and everything else still runs.</summary>
    public static void RestoreMachineSettings()
    {
        // The Inno uninstaller runs --uninstall-restore unelevated
        // (PrivilegesRequired=lowest): without this hand-off the HKLM writes
        // below always fail silently and uninstall would leave UAC prompts
        // disabled and lock-on-wake off. Route only when provably unelevated —
        // null (unknown) must not spawn a child that could loop forever.
        try
        {
            if (ElevationCheck.IsCurrentProcessElevated() == false && NeedsElevatedRestore())
            {
                if (SelfElevation.RunElevatedAction("--uninstall-restore", "Uninstall restore"))
                {
                    // The elevated instance ran this whole method with full rights.
                    return;
                }
                Log.Warn("Uninstall restore: elevation declined — UAC/lock-on-wake settings left as-is.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: elevated hand-off failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            DisplayScale.RestoreSaved(config);
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: display scaling failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            if (config.PreviousUacSnapshotCaptured && UacSettings.Read().PromptsDisabled)
            {
                Log.Info("Uninstall restore: restoring UAC prompt level.");
                UacSettings.ApplyDirect(disablePrompts: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: UAC failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            if (config.PreviousLockOnWakeSnapshotCaptured && LockScreenSettings.SignInOnWakeDisabled())
            {
                Log.Info("Uninstall restore: restoring lock-on-wake.");
                LockScreenSettings.ApplyDirect(disableSignInOnWake: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: lock-on-wake failed: {ex.Message}");
        }

        try
        {
            SlateMode.ApplyDesktopMode();
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: slate mode failed: {ex.Message}");
        }
    }

    /// <summary>True when a snapshot exists whose restore needs an HKLM write —
    /// the same conditions the restore steps themselves check.</summary>
    private static bool NeedsElevatedRestore()
    {
        try
        {
            var config = ConfigStore.Load();
            return (config.PreviousUacSnapshotCaptured && UacSettings.Read().PromptsDisabled)
                || (config.PreviousLockOnWakeSnapshotCaptured && LockScreenSettings.SignInOnWakeDisabled());
        }
        catch
        {
            return false;
        }
    }

    private static void CreateShortcut()
    {
        // .lnk creation needs IShellLink (COM). Spawning Windows PowerShell for this
        // one-shot task avoids in-process COM interop under NativeAOT.
        // '' doubling: an apostrophe in the profile path (O'Brien) must not break
        // the single-quoted PS literals.
        var shortcut = ShortcutPath.Replace("'", "''");
        var exe = InstalledExePath.Replace("'", "''");
        var dir = InstallDir.Replace("'", "''");
        var script =
            "$ws = New-Object -ComObject WScript.Shell; " +
            $"$s = $ws.CreateShortcut('{shortcut}'); " +
            $"$s.TargetPath = '{exe}'; " +
            $"$s.WorkingDirectory = '{dir}'; " +
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
            // The NativeAOT payload is mostly native sibling DLLs — count them too.
            long bytes = 0;
            foreach (var file in Directory.GetFiles(InstallDir))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    bytes += new FileInfo(file).Length;
                }
            }
            key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }
}
