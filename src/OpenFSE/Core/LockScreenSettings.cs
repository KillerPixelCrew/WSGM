using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace OpenFSE.Core;

/// <summary>Controls whether Windows demands a sign-in after the screen turns off or
/// the device wakes from standby — the "Require a password on wakeup" power setting
/// (CONSOLELOCK). On modern-standby handhelds this setting is hidden from the classic
/// power UI but still applies, so it is written two ways:
///
///  1. The active power scheme's value (via powercfg, AC and DC).
///  2. The matching Power *policy* values in HKLM, which override the scheme and —
///     importantly on handhelds — survive vendor software switching power schemes.
///
/// This does not remove the lock screen you get from pressing Win+L; it stops the
/// device from locking itself when the screen sleeps.</summary>
public static class LockScreenSettings
{
    private const string ConsoleLockGuid = "0e796bdb-100d-47d6-a2d5-f7d2daa51f51";
    private const string SubNoneGuid = "fea3413e-7e05-4911-9a71-700331f1c294";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + ConsoleLockGuid;
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    /// <summary>True when waking the device does NOT require signing in again.</summary>
    public static bool SignInOnWakeDisabled()
    {
        try
        {
            // Policy wins over the scheme value when present.
            using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
            {
                if (policy?.GetValue("ACSettingIndex") is int policyAc)
                {
                    var policyDc = policy.GetValue("DCSettingIndex") as int? ?? policyAc;
                    return policyAc == 0 && policyDc == 0;
                }
            }

            using var schemes = Registry.LocalMachine.OpenSubKey(SchemesKey);
            if (schemes?.GetValue("ActivePowerScheme") is not string active)
            {
                return false;
            }
            using var setting = schemes.OpenSubKey($@"{active}\{SubNoneGuid}\{ConsoleLockGuid}");
            if (setting is null)
            {
                return false;   // absent = Windows default = require sign-in
            }
            var ac = setting.GetValue("ACSettingIndex") as int? ?? 1;
            var dc = setting.GetValue("DCSettingIndex") as int? ?? 1;
            return ac == 0 && dc == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read lock-on-wake setting: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs in the ELEVATED instance.</summary>
    public static bool ApplyDirect(bool disableSignInOnWake)
    {
        try
        {
            var config = ConfigStore.Load();

            if (disableSignInOnWake)
            {
                if (!config.PreviousLockOnWakeSnapshotCaptured)
                {
                    config.PreviousLockOnWakeSnapshotCaptured = true;
                    config.PreviousLockOnWakeRequired = !SignInOnWakeDisabled();
                    ConfigStore.Save(config);
                }

                using (var policy = Registry.LocalMachine.CreateSubKey(PolicyKey))
                {
                    policy.SetValue("ACSettingIndex", 0, RegistryValueKind.DWord);
                    policy.SetValue("DCSettingIndex", 0, RegistryValueKind.DWord);
                }
                SetSchemeValue(0);
                Log.Info("Sign-in on wake disabled (CONSOLELOCK=0, policy + active scheme).");
            }
            else
            {
                using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true))
                {
                    policy?.DeleteValue("ACSettingIndex", throwOnMissingValue: false);
                    policy?.DeleteValue("DCSettingIndex", throwOnMissingValue: false);
                }
                // Restore Windows' default (require sign-in) unless it was already off
                // before OpenFSE touched it.
                var restoreToRequired = !config.PreviousLockOnWakeSnapshotCaptured || config.PreviousLockOnWakeRequired;
                SetSchemeValue(restoreToRequired ? 1 : 0);

                config.PreviousLockOnWakeSnapshotCaptured = false;
                ConfigStore.Save(config);
                Log.Info($"Sign-in on wake restored (CONSOLELOCK={(restoreToRequired ? 1 : 0)}).");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change lock-on-wake setting", ex);
            return false;
        }
    }

    private static void SetSchemeValue(int index)
    {
        RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {index}");
        RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {index}");
        RunPowerCfg("/setactive SCHEME_CURRENT");
    }

    private static void RunPowerCfg(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            Log.Warn($"powercfg {arguments} failed: {ex.Message}");
        }
    }

    /// <summary>Requests the change from the non-elevated UI (one elevation prompt).</summary>
    public static bool RequestChange(bool disableSignInOnWake)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(exe,
                disableSignInOnWake ? "--disable-lock-on-wake" : "--restore-lock-on-wake")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
            return p?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lock-on-wake change not applied: {ex.Message}");
            return false;
        }
    }
}
