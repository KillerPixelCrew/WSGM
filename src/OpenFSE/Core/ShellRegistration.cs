using System;
using Microsoft.Win32;

namespace OpenFSE.Core;

/// <summary>Manages the per-user Winlogon Shell value. HKCU only — no admin rights
/// needed, other accounts untouched. The pre-existing value (if any) is preserved in
/// config and restored exactly on uninstall/panic.</summary>
public static class ShellRegistration
{
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ShellValue = "Shell";
    private const string GamingConfigKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string StartupToGamingHome = "StartupToGamingHome";

    public static string OwnShellCommand => $"\"{Environment.ProcessPath}\" --shell";

    public static string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return key?.GetValue(ShellValue) as string;
    }

    public static bool IsInstalledForThisExe()
    {
        var current = CurrentValue();
        var exe = Environment.ProcessPath;
        return current is not null
            && exe is not null
            && current.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Registers OpenFSE as this user's shell. Saves any pre-existing custom
    /// Shell value into config first so uninstall can restore it exactly.</summary>
    public static void Install(AppConfig config)
    {
        var existing = CurrentValue();
        if (existing is not null && !existing.Contains("OpenFSE", StringComparison.OrdinalIgnoreCase))
        {
            config.PreviousShellValue = existing;
        }

        using (var key = Registry.CurrentUser.CreateSubKey(WinlogonKey))
        {
            key.SetValue(ShellValue, OwnShellCommand, RegistryValueKind.String);
        }

        // Prevent Xbox FSE from fighting us at boot.
        using (var gaming = Registry.CurrentUser.CreateSubKey(GamingConfigKey))
        {
            gaming.SetValue(StartupToGamingHome, 0, RegistryValueKind.DWord);
        }

        ConfigStore.Save(config);
        Log.Info($"Installed as shell: {OwnShellCommand} (previous: {config.PreviousShellValue ?? "<default>"})");
    }

    /// <summary>Restores the previous shell registration (delete our value, or write back
    /// the saved pre-existing one). Safe to call from a broken state — reads config
    /// defensively and never throws.</summary>
    public static void Uninstall()
    {
        try
        {
            string? previous = null;
            try { previous = ConfigStore.Load().PreviousShellValue; } catch { }

            using var key = Registry.CurrentUser.CreateSubKey(WinlogonKey);
            if (!string.IsNullOrEmpty(previous))
            {
                key.SetValue(ShellValue, previous, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ShellValue, throwOnMissingValue: false);
            }
            Log.Info($"Shell registration restored (previous: {previous ?? "<default explorer>"})");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore shell registration", ex);
        }
    }
}
