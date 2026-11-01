using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Owns persisted recovery intent for Windows wake sign-in policy.</summary>
internal static class LockScreenSettings
{
    /// <summary>Reports confirmed disabled sign-in, or false when Windows state is unavailable.</summary>
    /// <returns>True only when the current Windows snapshot reports sign-in disabled; false also covers read failures.</returns>
    public static bool SignInOnWakeDisabled()
    {
        try
        {
            return WindowsWakeSecurity.IsSignInDisabled(WindowsWakeSecurity.Capture());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read lock-on-wake setting: {ex.Message}");
            return false;
        }
    }

    /// <summary>Applies an explicitly requested elevated change, persisting recovery before Windows writes.</summary>
    /// <param name="store">The process-owned configuration persistence the recovery state is saved to.</param>
    /// <param name="disableSignInOnWake">True disables sign-in; false restores saved state.</param>
    /// <returns>False on failure. Saved recovery state remains available after a failed restore.</returns>
    public static bool ApplyDirect(ConfigStore store, bool disableSignInOnWake)
    {
        try
        {
            using var operation = new WindowsPolicyOperation("LockScreenSettings");
            if (disableSignInOnWake)
            {
                var snapshot = WindowsWakeSecurity.Capture();
                store.Update(fresh =>
                {
                    if (!fresh.PreviousLockOnWakeSnapshotCaptured)
                    {
                        CaptureInto(fresh, snapshot);
                    }

                    return true;
                });

                DisableSignIn();
            }
            else
            {
                var saved = store.Read().RequireConfig();
                var snapshot = RecoverySnapshot(saved);
                var failures = saved.PreviousLockOnWakeSnapshotCaptured
                    ? Describe(WindowsWakeSecurity.Restore(snapshot).Failures)
                    : RestoreSecureDefault(saved.PreviousNoLockScreen);
                if (failures.Count > 0)
                {
                    // Every step was attempted once; the saved snapshot stays for the next restore.
                    foreach (var failure in failures)
                    {
                        Log.Warn($"Lock-on-wake restore: {failure}");
                    }

                    Log.Error($"Sign-in on wake was not fully restored ({failures.Count} step(s) failed).");
                    return false;
                }

                store.Update(fresh =>
                {
                    var current = RecoverySnapshot(fresh);
                    if (fresh.PreviousLockOnWakeSnapshotCaptured != saved.PreviousLockOnWakeSnapshotCaptured
                        || current.PolicyExisted != snapshot.PolicyExisted
                        || current.PolicyAc != snapshot.PolicyAc || current.PolicyDc != snapshot.PolicyDc
                        || current.NoLockScreen != snapshot.NoLockScreen
                        || !current.Schemes.SequenceEqual(snapshot.Schemes))
                    {
                        return true;
                    }

                    fresh.PreviousLockOnWakeSnapshotCaptured = false;
                    fresh.PreviousConsoleLockSchemeValues = [];
                    fresh.PreviousConsoleLockPolicyKeyExisted = false;
                    fresh.PreviousConsoleLockPolicyAc = -1;
                    fresh.PreviousConsoleLockPolicyDc = -1;
                    fresh.PreviousNoLockScreen = -1;
                    return true;
                });
            }

            Log.Info($"Sign-in on wake {(disableSignInOnWake ? "disabled" : "restored")}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change lock-on-wake setting", ex);
            return false;
        }
    }

    /// <summary>Copies wake-security recovery facts into a mutable configuration without saving it.</summary>
    /// <param name="config">Exclusive caller-owned configuration to mutate.</param>
    /// <param name="snapshot">Pre-change Windows state, including every captured scheme.</param>
    internal static void CaptureInto(AppConfig config, WakeSecuritySnapshot snapshot)
    {
        config.PreviousConsoleLockSchemeValues =
        [
            .. snapshot.Schemes.Select(scheme => new PowerSchemeConsoleLock
                { SchemeGuid = scheme.Scheme.ToString("D"), AcValue = scheme.Ac, DcValue = scheme.Dc })
        ];
        config.PreviousConsoleLockPolicyKeyExisted = snapshot.PolicyExisted;
        config.PreviousConsoleLockPolicyAc = snapshot.PolicyAc;
        config.PreviousConsoleLockPolicyDc = snapshot.PolicyDc;
        config.PreviousNoLockScreen = snapshot.NoLockScreen;
        config.PreviousLockOnWakeSnapshotCaptured = true;
    }

    /// <summary>Reconstructs the Windows wake-security snapshot represented by persisted recovery fields.</summary>
    /// <param name="config">Configuration containing recovery state, including legacy sentinel values.</param>
    /// <returns>A recovery snapshot; missing capture uses the legacy policy and empty scheme fallback.</returns>
    /// <exception cref="FormatException">A saved scheme identifier is not a GUID.</exception>
    internal static WakeSecuritySnapshot RecoverySnapshot(AppConfig config)
    {
        return new WakeSecuritySnapshot(
            !config.PreviousLockOnWakeSnapshotCaptured || config.PreviousConsoleLockPolicyKeyExisted,
            config.PreviousLockOnWakeSnapshotCaptured ? config.PreviousConsoleLockPolicyAc : -1,
            config.PreviousLockOnWakeSnapshotCaptured ? config.PreviousConsoleLockPolicyDc : -1,
            config.PreviousNoLockScreen,
            config.PreviousLockOnWakeSnapshotCaptured
                ? config.PreviousConsoleLockSchemeValues.Select(scheme =>
                    new WakeSecurityScheme(Guid.Parse(scheme.SchemeGuid), scheme.AcValue, scheme.DcValue)).ToArray()
                : []);
    }

    /// <summary>Requests one elevation prompt from the non-elevated UI.</summary>
    /// <param name="disableSignInOnWake">Whether to disable or restore wake sign-in.</param>
    /// <returns>Whether the elevated helper succeeded.</returns>
    public static bool RequestChange(bool disableSignInOnWake)
    {
        return SelfElevation.RunElevatedAction(
            disableSignInOnWake ? "--disable-lock-on-wake" : "--restore-lock-on-wake",
            "Lock-on-wake change");
    }

    /// <summary>
    ///     Disables wake sign-in in the order Windows needs: the policy for every scheme, each installed
    ///     scheme's own value, the refresh that applies those, then the lock screen itself. The first
    ///     failure stops it; the snapshot saved before it is what restores.
    /// </summary>
    private static void DisableSignIn()
    {
        WindowsWakeSecurity.SetConsoleLockPolicy(0, 0);
        foreach (var scheme in SchemesOrActive())
        {
            WindowsWakeSecurity.SetSchemeConsoleLock(scheme, 0, 0);
        }

        WindowsPower.RefreshActiveScheme();
        WindowsWakeSecurity.SetNoLockScreen(1);
    }

    /// <summary>
    ///     Restores Windows' secure default when no snapshot was saved: no console-lock policy values, sign-in on
    ///     wake in every scheme, the active scheme refreshed so that takes effect, and the saved personalization
    ///     value (deleted when none was saved). Every step is attempted once, even after an earlier one fails.
    /// </summary>
    /// <param name="noLockScreen">The saved NoLockScreen value, or -1 to delete it.</param>
    /// <returns>A description of each step that failed; empty when all were written.</returns>
    private static List<string> RestoreSecureDefault(int noLockScreen)
    {
        List<string> failures = [];
        Attempt("console-lock policy", static () => WindowsWakeSecurity.SetConsoleLockPolicy(-1, -1));
        IReadOnlyList<Guid> schemes = [];
        Attempt("scheme list", () => schemes = SchemesOrActive());
        foreach (var scheme in schemes)
        {
            Attempt($"scheme {scheme:D}", () => WindowsWakeSecurity.SetSchemeConsoleLock(scheme, 1, 1));
        }

        Attempt("active scheme refresh", WindowsPower.RefreshActiveScheme);
        Attempt("NoLockScreen", () => WindowsWakeSecurity.SetNoLockScreen(noLockScreen));
        return failures;

        void Attempt(string step, Action write)
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                failures.Add($"{step} failed: {ex.Message}");
            }
        }
    }

    private static List<string> Describe(IReadOnlyList<WakeSecurityRestoreFailure> failures)
    {
        return
        [
            .. failures.Select(failure => failure.Scheme is { } scheme
                ? $"{failure.Setting} {scheme:D} failed: {failure.Error.Message}"
                : $"{failure.Setting} failed: {failure.Error.Message}")
        ];
    }

    /// <summary>Every installed scheme, or the active one alone when Windows lists none.</summary>
    private static IReadOnlyList<Guid> SchemesOrActive()
    {
        var schemes = WindowsPower.EnumerateSchemes();
        return schemes.Count > 0 ? schemes : [WindowsPower.GetActiveScheme()];
    }
}
