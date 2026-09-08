using System;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Owns persisted recovery intent for Windows wake sign-in policy.</summary>
public static class LockScreenSettings
{
    /// <summary>Reports confirmed disabled sign-in, or false when Windows state is unavailable.</summary>
    public static bool SignInOnWakeDisabled()
    {
        try { return WindowsWakeSecurity.IsSignInDisabled(WindowsWakeSecurity.Capture()); }
        catch (Exception ex) { Log.Warn($"Could not read lock-on-wake setting: {ex.Message}"); return false; }
    }

    /// <summary>Applies an explicitly requested elevated change, persisting recovery before Windows writes.</summary>
    /// <param name="disableSignInOnWake">True disables sign-in; false restores saved state.</param>
    /// <returns>False on failure. Saved recovery state remains available after a failed restore.</returns>
    public static bool ApplyDirect(bool disableSignInOnWake)
    {
        try
        {
            var config = ConfigStore.LoadForMutation();
            if (disableSignInOnWake)
            {
                if (!config.PreviousLockOnWakeSnapshotCaptured)
                {
                    CaptureInto(config, WindowsWakeSecurity.Capture());
                    ConfigStore.Save(config);
                }
                WindowsWakeSecurity.DisableSignIn();
            }
            else
            {
                WindowsWakeSecurity.Restore(RecoverySnapshot(config));
                config.PreviousLockOnWakeSnapshotCaptured = false;
                config.PreviousConsoleLockSchemeValues = [];
                config.PreviousConsoleLockPolicyKeyExisted = false;
                config.PreviousConsoleLockPolicyAc = -1;
                config.PreviousConsoleLockPolicyDc = -1;
                config.PreviousNoLockScreen = -1;
                ConfigStore.Save(config);
            }
            Log.Info($"Sign-in on wake {(disableSignInOnWake ? "disabled" : "restored")}.");
            return true;
        }
        catch (Exception ex) { Log.Error("Failed to change lock-on-wake setting", ex); return false; }
    }

    internal static void CaptureInto(AppConfig config, WakeSecuritySnapshot snapshot)
    {
        config.PreviousConsoleLockSchemeValues = snapshot.Schemes.Select(scheme => new PowerSchemeConsoleLock
        { SchemeGuid = scheme.Scheme.ToString("D"), AcValue = scheme.Ac, DcValue = scheme.Dc }).ToList();
        config.PreviousConsoleLockPolicyKeyExisted = snapshot.PolicyExisted;
        config.PreviousConsoleLockPolicyAc = snapshot.PolicyAc;
        config.PreviousConsoleLockPolicyDc = snapshot.PolicyDc;
        config.PreviousNoLockScreen = snapshot.NoLockScreen;
        config.PreviousLockOnWakeSnapshotCaptured = true;
    }

    internal static WakeSecuritySnapshot RecoverySnapshot(AppConfig config) =>
        new(!config.PreviousLockOnWakeSnapshotCaptured || config.PreviousConsoleLockPolicyKeyExisted,
            config.PreviousLockOnWakeSnapshotCaptured ? config.PreviousConsoleLockPolicyAc : -1,
            config.PreviousLockOnWakeSnapshotCaptured ? config.PreviousConsoleLockPolicyDc : -1,
            config.PreviousNoLockScreen,
            config.PreviousLockOnWakeSnapshotCaptured
                ? config.PreviousConsoleLockSchemeValues.Select(scheme =>
                    new WakeSecurityScheme(Guid.Parse(scheme.SchemeGuid), scheme.AcValue, scheme.DcValue)).ToArray()
                : []);

    /// <summary>Requests one elevation prompt from the non-elevated UI.</summary>
    /// <param name="disableSignInOnWake">Whether to disable or restore wake sign-in.</param>
    /// <returns>Whether the elevated helper succeeded.</returns>
    public static bool RequestChange(bool disableSignInOnWake) =>
        SelfElevation.RunElevatedAction(disableSignInOnWake ? "--disable-lock-on-wake" : "--restore-lock-on-wake",
            "Lock-on-wake change");
}
