using System;
using SteamInputLease;

namespace WSGM.Core;

/// <summary>Process-wide owner of WSGM's Steam Input block lease.
/// The injected gate runs only in Steam and prevents Steam Input from opening
/// controllers while a focus-taking WSGM surface needs SDL to read them. The
/// pipe-backed lease is released automatically if WSGM crashes.</summary>
public static class SteamInputBlocker
{
    /// <summary>Displayed when the authoritative host-side probe cannot safely
    /// resolve the current Steam build for controller recovery.</summary>
    public const string DynamicRecoveryWarning = "Steam Input could not dynamically locate Steam's controller-release code. Please report this on GitHub — the Steam Input hook may need updating.";

    private static readonly object Sync = new();
    private static SteamInputClient? _client;
    private static SteamInputBlockLease? _lease;

    /// <summary>Raised when the authoritative dynamic Steam recovery probe or
    /// its guarded controller-rescan operation fails.</summary>
    public static event Action<string>? RecoveryWarningRaised;

    /// <summary>True while this process owns an active Steam Input block lease.</summary>
    public static bool IsApplied
    {
        get
        {
            lock (Sync)
            {
                return _lease is not null;
            }
        }
    }

    /// <summary>Acquires the shared lease when Steam is available. Failures are
    /// logged and leave the UI alive so the device report can identify the
    /// target-process or integrity-level mismatch.</summary>
    public static void Acquire()
    {
        lock (Sync)
        {
            if (_lease is not null)
            {
                return;
            }

            try
            {
                _client ??= new SteamInputClient();
                _lease = _client.Acquire();
                Log.Info($"Steam Input lease acquired (revoked {_lease.InitialStatus.LastRevokedHandleCount} HID handles).");
                if (!_lease.InitialStatus.SupportsInternalRecovery)
                {
                    CheckHostRecoveryBestEffort();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Steam Input lease acquisition failed.", ex);
            }
        }
    }

    /// <summary>Releases the shared lease and asks the gate to resume Steam's
    /// controller discovery. Never throws because it runs during shutdown.</summary>
    public static void ReleaseBestEffort(string reason)
    {
        lock (Sync)
        {
            if (_lease is null)
            {
                return;
            }

            var lease = _lease;
            _lease = null;
            try
            {
                // Release already performs recovery: a payload advertising
                // internal recovery schedules discovery on its own timer, and
                // one without it makes the host run the guarded two-pass scan
                // inline. Repeating it here only bought a second multi-second
                // scan of Steam's address space, which the next overlay open
                // then waited on.
                var outcome = lease.Release();
                Log.Info($"Steam Input lease released ({reason}; {outcome.Status.LeaseCount} active " +
                         $"leases remain; recovery {DescribeRecovery(outcome)}).");
                if (!outcome.RecoveryRequested)
                {
                    // Blocking is lifted — Steam keeps working, it just has not
                    // been told to look for controllers again, so a pad can stay
                    // missing in Steam until it notices by itself.
                    Log.Warn($"Steam Input controller recovery did not run ({reason}): {outcome.RecoveryMessage}");
                    RaiseRecoveryWarning();
                }
            }
            catch (Exception ex)
            {
                // The release handshake itself failed. The SafeHandle/pipe
                // lifetime still makes that crash-safe; preserve shutdown and
                // capture the diagnosis.
                lease.Dispose();
                Log.Error($"Steam Input lease release failed ({reason}).", ex);
            }
        }
    }

    private static string DescribeRecovery(SteamInputReleaseOutcome outcome) => outcome.Recovery switch
    {
        SteamControllerRecovery.Scheduled => "scheduled by the payload",
        SteamControllerRecovery.Completed => outcome.Rescan is { } rescan
            ? $"run by the host (scans {rescan.ScanCountBefore}→{rescan.ScanCountAfter})"
            : "run by the host",
        SteamControllerRecovery.NotRequired => "not required",
        _ => "UNAVAILABLE",
    };

    /// <summary>Uses the host resolver rather than the payload capability bit as
    /// the compatibility authority. The payload and host resolve independently;
    /// a missing in-process target does not mean WSGM cannot restore Steam input.</summary>
    private static void CheckHostRecoveryBestEffort()
    {
        try
        {
            _client?.CheckRecovery();
            Log.Info("Steam Input host recovery probe succeeded.");
        }
        catch (Exception ex)
        {
            Log.Error("Steam Input host recovery probe failed.", ex);
            RaiseRecoveryWarning();
        }
    }

    private static void RaiseRecoveryWarning()
    {
        Log.Warn("Steam Input dynamic controller-recovery resolver is unavailable; GitHub report requested.");
        RecoveryWarningRaised?.Invoke(DynamicRecoveryWarning);
    }
}
