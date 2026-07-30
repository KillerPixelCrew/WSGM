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

    private sealed record StringRegistryValue(bool Exists, string? Value, RegistryValueKind Kind);
    private sealed record IntRegistryValue(bool Exists, int Value, RegistryValueKind Kind);

    public static string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return ReadStringValue(key, ShellValue).Value;
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
        using var shell = Registry.CurrentUser.CreateSubKey(WinlogonKey);
        var existingShell = ReadStringValue(shell, ShellValue);
        var installingOverOurShell = IsOwnedByThisExe(existingShell.Value);

        // Never overwrite the original snapshots during an idempotent install.
        // Persist them before changing either registry value: if saving fails, the
        // old shell is still intact and recovery remains possible.
        if (!installingOverOurShell)
        {
            config.PreviousShellValue = existingShell.Value;
            config.PreviousShellSnapshotCaptured = true;
            config.PreviousShellValueExists = existingShell.Exists;
            config.PreviousShellValueKind = existingShell.Kind;

            using var gamingSnapshot = Registry.CurrentUser.CreateSubKey(GamingConfigKey);
            var existingGaming = ReadIntValue(gamingSnapshot, StartupToGamingHome);
            config.PreviousStartupToGamingHomeValue = existingGaming.Value;
            config.PreviousStartupToGamingHomeSnapshotCaptured = true;
            config.PreviousStartupToGamingHomeValueExists = existingGaming.Exists;
            config.PreviousStartupToGamingHomeValueKind = existingGaming.Kind;
        }

        ConfigStore.Save(config);

        shell.SetValue(ShellValue, OwnShellCommand, RegistryValueKind.String);

        // Prevent Xbox FSE from fighting us at boot.
        using (var gaming = Registry.CurrentUser.CreateSubKey(GamingConfigKey))
        {
            gaming.SetValue(StartupToGamingHome, 0, RegistryValueKind.DWord);
        }

        Log.Info($"Installed as shell: {OwnShellCommand} (previous: {DisplayShellSnapshot(config)})");
    }

    /// <summary>Restores the previous shell registration (delete our value, or write back
    /// the saved pre-existing one). Safe to call from a broken state — reads config
    /// defensively and never throws.</summary>
    public static void Uninstall()
    {
        try
        {
            var config = new AppConfig();
            try { config = ConfigStore.Load(); } catch { }

            using var key = Registry.CurrentUser.CreateSubKey(WinlogonKey);
            var current = ReadStringValue(key, ShellValue);
            if (IsOwnedByThisExe(current.Value))
            {
                // Older config files stored a non-null value without an explicit
                // presence bit. Keep them recoverable while all new snapshots use
                // the precise captured/exists pair.
                var hasPreviousShell = config.PreviousShellValueExists ||
                    (!config.PreviousShellSnapshotCaptured && config.PreviousShellValue is not null);
                if (hasPreviousShell)
                {
                    key.SetValue(ShellValue, config.PreviousShellValue ?? string.Empty,
                        NormalizeStringKind(config.PreviousShellValueKind));
                }
                else
                {
                    key.DeleteValue(ShellValue, throwOnMissingValue: false);
                }
            }

            using var gaming = Registry.CurrentUser.CreateSubKey(GamingConfigKey);
            if (config.PreviousStartupToGamingHomeSnapshotCaptured &&
                config.PreviousStartupToGamingHomeValueExists)
            {
                gaming.SetValue(StartupToGamingHome, config.PreviousStartupToGamingHomeValue,
                    NormalizeDwordKind(config.PreviousStartupToGamingHomeValueKind));
            }
            else if (config.PreviousStartupToGamingHomeSnapshotCaptured)
            {
                gaming.DeleteValue(StartupToGamingHome, throwOnMissingValue: false);
            }
            Log.Info($"Shell registration restored (previous: {DisplayShellSnapshot(config)})");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore shell registration", ex);
        }
    }

    private static StringRegistryValue ReadStringValue(RegistryKey? key, string valueName)
    {
        if (key is null)
        {
            return new StringRegistryValue(false, null, RegistryValueKind.String);
        }

        var sentinel = new object();
        var value = key.GetValue(valueName, sentinel, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (ReferenceEquals(value, sentinel))
        {
            return new StringRegistryValue(false, null, RegistryValueKind.String);
        }
        return new StringRegistryValue(true, value as string ?? string.Empty, key.GetValueKind(valueName));
    }

    private static IntRegistryValue ReadIntValue(RegistryKey? key, string valueName)
    {
        if (key is null)
        {
            return new IntRegistryValue(false, 0, RegistryValueKind.DWord);
        }

        var sentinel = new object();
        var value = key.GetValue(valueName, sentinel, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (ReferenceEquals(value, sentinel))
        {
            return new IntRegistryValue(false, 0, RegistryValueKind.DWord);
        }
        return new IntRegistryValue(true, value is int number ? number : 0, key.GetValueKind(valueName));
    }

    private static bool IsOwnedByThisExe(string? value)
    {
        var exe = Environment.ProcessPath;
        return value is not null && exe is not null && value.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryValueKind NormalizeStringKind(RegistryValueKind kind)
        => kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String;

    private static RegistryValueKind NormalizeDwordKind(RegistryValueKind kind)
        => RegistryValueKind.DWord;

    private static string DisplayShellSnapshot(AppConfig config)
        => !(config.PreviousShellValueExists ||
             (!config.PreviousShellSnapshotCaptured && config.PreviousShellValue is not null))
            ? "<absent>"
            : config.PreviousShellValue ?? string.Empty;
}
