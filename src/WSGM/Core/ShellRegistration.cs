using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Manages the per-user Winlogon Shell value. HKCU only — no admin rights
/// needed, other accounts untouched. The pre-existing value (if any) is preserved in
/// config and restored exactly on uninstall/panic.</summary>
public static class ShellRegistration
{
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ShellValue = "Shell";
    private const string GamingConfigKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string StartupToGamingHome = "StartupToGamingHome";

    /// <summary>The registered command always prefers the INSTALLED copy (stable
    /// path) over wherever the current process happens to run from. ProcessPath can
    /// be null in exotic hosts — fall back to the base directory rather than
    /// registering a broken '"" --shell' value.</summary>
    public static string OwnShellCommand
    {
        get
        {
            var exe = Installer.IsAppInstalled
                ? Installer.InstalledExePath
                : Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "WSGM.exe");
            return $"\"{exe}\" --shell";
        }
    }

    private sealed record StringRegistryValue(bool Exists, string? Value, RegistryValueKind Kind);
    private sealed record IntRegistryValue(bool Exists, int Value, RegistryValueKind Kind);

    public static string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return ReadStringValue(key, ShellValue).Value;
    }

    public static bool IsInstalledForThisExe() => IsOwnedByThisExe(CurrentValue());

    /// <summary>Registers WSGM as this user's shell. Saves any pre-existing custom
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

            // Read-only snapshot — OpenSubKey so a pure read can't materialize the key.
            using var gamingSnapshot = Registry.CurrentUser.OpenSubKey(GamingConfigKey);
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

            // OpenSubKey (not CreateSubKey): a restore that finds nothing to restore
            // must not create keys as a side effect. Winlogon always exists; a null
            // here also means our value can't be registered there.
            using (var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, writable: true))
            {
                if (key is not null && IsOwnedByThisExe(ReadStringValue(key, ShellValue).Value))
                {
                    if (HasPreviousShell(config))
                    {
                        key.SetValue(ShellValue, config.PreviousShellValue ?? string.Empty,
                            NormalizeStringKind(config.PreviousShellValueKind));
                    }
                    else
                    {
                        key.DeleteValue(ShellValue, throwOnMissingValue: false);
                    }
                }
            }

            using (var gaming = Registry.CurrentUser.OpenSubKey(GamingConfigKey, writable: true))
            {
                if (gaming is not null && config.PreviousStartupToGamingHomeSnapshotCaptured)
                {
                    // Revert only while the value is still the 0 WSGM wrote in
                    // Install — anything else means the user (or the Xbox app)
                    // changed it since, and that change must win.
                    var currentGaming = ReadIntValue(gaming, StartupToGamingHome);
                    if (currentGaming.Exists && currentGaming.Value == 0)
                    {
                        if (config.PreviousStartupToGamingHomeValueExists)
                        {
                            gaming.SetValue(StartupToGamingHome, config.PreviousStartupToGamingHomeValue,
                                NormalizeDwordKind(config.PreviousStartupToGamingHomeValueKind));
                        }
                        else
                        {
                            gaming.DeleteValue(StartupToGamingHome, throwOnMissingValue: false);
                        }
                    }
                }
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
        // Ours if the registered COMMAND'S EXECUTABLE is the running copy or the
        // installed copy. Path equality, not substring — a foreign command that
        // merely mentions our path (e.g. a wrapper passing it as an argument)
        // must not be treated as ours and deleted on uninstall.
        var registeredExe = ExtractExecutablePath(value);
        if (registeredExe is null)
        {
            return false;
        }
        var exe = Environment.ProcessPath;
        return (exe is not null && string.Equals(registeredExe, exe, StringComparison.OrdinalIgnoreCase))
            || string.Equals(registeredExe, Installer.InstalledExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the executable out of a Shell command line: the quoted token
    /// if the command starts with a quote, otherwise everything up to the first
    /// space (matching how Winlogon itself launches the value).</summary>
    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var closing = command.IndexOf('"', 1);
            return closing > 1 ? command[1..closing] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static RegistryValueKind NormalizeStringKind(RegistryValueKind kind)
        => kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String;

    private static RegistryValueKind NormalizeDwordKind(RegistryValueKind kind)
        => kind == RegistryValueKind.QWord ? RegistryValueKind.QWord : RegistryValueKind.DWord;

    /// <summary>Older config files stored a non-null value without an explicit
    /// presence bit. Keep them recoverable while all new snapshots use the precise
    /// captured/exists pair.</summary>
    private static bool HasPreviousShell(AppConfig config)
        => config.PreviousShellValueExists ||
           (!config.PreviousShellSnapshotCaptured && config.PreviousShellValue is not null);

    private static string DisplayShellSnapshot(AppConfig config)
        => HasPreviousShell(config)
            ? config.PreviousShellValue ?? string.Empty
            : "<absent>";
}
