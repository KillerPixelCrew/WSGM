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

    private static readonly RegistryValueSnapshot<string?> ShellSnapshot = new(
        ShellValue,
        absentValue: null,
        writeFallback: string.Empty,
        defaultKind: RegistryValueKind.String,
        coerce: static value => value as string ?? string.Empty,
        normalizeKind: static kind =>
            kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String,
        load: static config => new(config.PreviousShellSnapshotCaptured, config.PreviousShellValueExists,
            config.PreviousShellValue, config.PreviousShellValueKind),
        store: static (config, state) =>
        {
            config.PreviousShellValue = state.Value;
            config.PreviousShellSnapshotCaptured = state.Captured;
            config.PreviousShellValueExists = state.Exists;
            config.PreviousShellValueKind = state.Kind;
        });

    private static readonly RegistryValueSnapshot<int> GamingHomeSnapshot = new(
        StartupToGamingHome,
        absentValue: 0,
        writeFallback: 0,
        defaultKind: RegistryValueKind.DWord,
        coerce: static value => value is int number ? number : 0,
        normalizeKind: static kind =>
            kind == RegistryValueKind.QWord ? RegistryValueKind.QWord : RegistryValueKind.DWord,
        load: static config => new(config.PreviousStartupToGamingHomeSnapshotCaptured,
            config.PreviousStartupToGamingHomeValueExists,
            config.PreviousStartupToGamingHomeValue,
            config.PreviousStartupToGamingHomeValueKind),
        store: static (config, state) =>
        {
            config.PreviousStartupToGamingHomeValue = state.Value;
            config.PreviousStartupToGamingHomeSnapshotCaptured = state.Captured;
            config.PreviousStartupToGamingHomeValueExists = state.Exists;
            config.PreviousStartupToGamingHomeValueKind = state.Kind;
        });

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

    /// <summary>Gets the current account-level Windows shell command, if one is set.</summary>
    public static string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return ShellSnapshot.ReadCurrent(key).Value;
    }

    /// <summary>Gets whether the current account's shell command points at this executable.</summary>
    public static bool IsInstalledForThisExe() => IsOwnedByThisExe(CurrentValue());

    /// <summary>Registers WSGM as this user's shell. Saves any pre-existing custom
    /// Shell value into config first so uninstall can restore it exactly.</summary>
    public static void Install(AppConfig config)
    {
        using var shell = Registry.CurrentUser.CreateSubKey(WinlogonKey);
        var existingShell = ShellSnapshot.ReadCurrent(shell);
        var installingOverOurShell = IsOwnedByThisExe(existingShell.Value);

        // Never overwrite the original snapshots during an idempotent install.
        // Persist them before changing either registry value: if saving fails, the
        // old shell is still intact and recovery remains possible.
        if (!installingOverOurShell)
        {
            ShellSnapshot.Capture(config, existingShell);

            // Read-only snapshot — OpenSubKey so a pure read can't materialize the key.
            using var gamingSnapshot = Registry.CurrentUser.OpenSubKey(GamingConfigKey);
            GamingHomeSnapshot.Capture(config, GamingHomeSnapshot.ReadCurrent(gamingSnapshot));
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
                if (key is not null && IsOwnedByThisExe(ShellSnapshot.ReadCurrent(key).Value))
                {
                    ShellSnapshot.Restore(key, config);
                }
            }

            using (var gaming = Registry.CurrentUser.OpenSubKey(GamingConfigKey, writable: true))
            {
                if (gaming is not null && GamingHomeSnapshot.IsCaptured(config))
                {
                    // Revert only while the value is still the 0 WSGM wrote in
                    // Install — anything else means the user (or the Xbox app)
                    // changed it since, and that change must win.
                    var currentGaming = GamingHomeSnapshot.ReadCurrent(gaming);
                    if (currentGaming.Exists && currentGaming.Value == 0)
                    {
                        GamingHomeSnapshot.Restore(gaming, config);
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
    internal static string? ExtractExecutablePath(string? command)
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

    private static string DisplayShellSnapshot(AppConfig config)
        => ShellSnapshot.HasValue(config)
            ? config.PreviousShellValue ?? string.Empty
            : "<absent>";
}
