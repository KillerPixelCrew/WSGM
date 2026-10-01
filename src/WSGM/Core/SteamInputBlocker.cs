using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamInterop;

namespace WSGM.Core;

/// <summary>
///     Process-wide owner of WSGM's Steam Input block lease.
///     The injected gate runs only in Steam and prevents Steam Input from opening
///     controllers while a focus-taking WSGM surface needs SDL to read them. The
///     pipe-backed lease is released automatically if WSGM crashes.
/// </summary>
public static class SteamInputBlocker
{
    /// <summary>
    ///     Displayed when the authoritative host-side probe cannot safely
    ///     resolve the current Steam build for controller recovery.
    /// </summary>
    private const string DynamicRecoveryWarning =
        "Steam Input could not dynamically locate Steam's controller-release code. Please report this on GitHub — the Steam Input hook may need updating.";

    private static readonly Lock Sync = new();
    private static readonly Lock OwnersSync = new();

    // The lease itself is process-wide, but several WSGM surfaces can need it at
    // the same time (the quick-access sheet and the settings window opened from
    // it). Each names itself here, so one surface closing cannot take the
    // controller away from another that is still on screen; see docs\steam-input.md.
    private static readonly HashSet<string> Owners = new(StringComparer.Ordinal);

    // Bounds how long shutdown waits for a surface release that is still running natively, so its
    // controller recovery can finish before the process exits.
    private static readonly TimeSpan PendingReleaseWait = TimeSpan.FromSeconds(15);

    private static SteamInputClient? _client;
    private static SteamInputBlockLease? _lease;
    private static int _nextOwnerId;

    // Surface releases run here, one after another, outside Sync. Guarded by Sync.
    private static Task _nativeRelease = Task.CompletedTask;

    // Brings the native lease in line with the owner set, one pass after another. Guarded by
    // OwnersSync.
    private static Task _reconcile = Task.CompletedTask;

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

    /// <summary>
    ///     Raised when the authoritative dynamic Steam recovery probe or
    ///     its guarded controller-rescan operation fails.
    /// </summary>
    public static event Action<string>? RecoveryWarningRaised;

    /// <summary>
    ///     Acquires the shared lease when a resident shim is available.
    ///     Failures are logged and leave the UI alive so the device report can identify
    ///     what was missing.
    /// </summary>
    /// <remarks>
    ///     WSGM.exe never injects. The block is delivered by the shim Steam loads from
    ///     its own directory, so with Steam Input Management off - or before Steam has
    ///     restarted since the shim was deployed - there is simply nothing to connect
    ///     to, and this fails open exactly like the Steam-unavailable path always did.
    /// </remarks>
    private static void Acquire()
    {
        lock (Sync)
        {
            if (_lease is not null)
            {
                return;
            }

            var shim = SteamInputShim.Probe();
            if (shim.State is not (SteamInputShimState.Deployed or SteamInputShimState.UpdatePending))
            {
                // UpdatePending is connectable on purpose: an older-but-ours shim is
                // the one Steam has mapped, and the protocol handshake is the
                // authority on whether it is compatible.
                Log.Warn(
                    $"Steam Input lease unavailable - no resident shim ({shim.State}" +
                    $"{(shim.Detail is null ? "" : $": {shim.Detail}")}). Surface opens unblocked.");
                return;
            }

            try
            {
                // AllowInjection stays false: this is what makes "WSGM never writes
                // into the Steam process" a property of the code rather than a promise.
                _client ??= new SteamInputClient(new SteamInputClientOptions { AllowInjection = false });
                _lease = _client.Acquire();
                if (shim.Vector != SteamInputShimVector.None)
                {
                    SteamInputShim.RecordLoad(shim.Vector);
                }

                Log.Info(
                    $"Steam Input lease acquired via {SteamInputShim.FileNameFor(shim.Vector)} (revoked {_lease.InitialStatus.LastRevokedHandleCount} HID handles).");
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

    /// <summary>Names a new surface owner, unique in this process.</summary>
    /// <param name="kind">What the surface is; the name appears in the lease log lines.</param>
    /// <returns>An owner name such as <c>settings-window#2</c>.</returns>
    public static string NewOwner(string kind)
    {
        return $"{kind}#{Interlocked.Increment(ref _nextOwnerId)}";
    }

    /// <summary>
    ///     Records <paramref name="owner" />'s claim and brings the lease up on a worker.
    ///     Never waits for a native operation, so a surface calls it on the UI thread the moment it
    ///     needs the controller. A surface opening over another one joins the live lease without
    ///     release/re-inject churn.
    /// </summary>
    /// <param name="owner">A name from <see cref="NewOwner" />.</param>
    public static void Hold(string owner)
    {
        lock (OwnersSync)
        {
            if (!Owners.Add(owner))
            {
                return;
            }

            Log.Info($"Steam Input lease claimed by {owner} ({Owners.Count} owner(s)).");
            Reconcile($"{owner} let go before the lease came up");
        }
    }

    /// <summary>
    ///     Ends <paramref name="owner" />'s claim. The lease is released on a worker once no other
    ///     owner holds it: a surface closing must never drop the controller block out from under a
    ///     surface that is still on screen (see <c>docs\steam-input.md</c>).
    /// </summary>
    /// <param name="owner">The owner whose claim ends.</param>
    /// <param name="reason">Why the claim ends; logged for device diagnosis.</param>
    /// <returns>Completes once the lease reflects the claim, including any claim ending before it.</returns>
    public static Task Drop(string owner, string reason)
    {
        lock (OwnersSync)
        {
            if (!Owners.Remove(owner))
            {
                return _reconcile;
            }

            if (Owners.Count > 0)
            {
                Log.Info($"Steam Input lease kept ({reason}; {owner} let go, still owned by " +
                         $"{string.Join(", ", Owners)}).");
            }

            return Reconcile(reason);
        }
    }

    // Called under OwnersSync. Each pass acts on the owner set as it is when the pass runs, so
    // claims that come and go faster than the native work settle on their final state.
    private static Task Reconcile(string reason)
    {
        return _reconcile = _reconcile.ContinueWith(_ =>
        {
            bool wanted;
            lock (OwnersSync)
            {
                wanted = Owners.Count > 0;
            }

            // A failed pass is logged, never left faulted: surfaces await this chain, and a
            // swallowed release is a lease that outlives every surface.
            try
            {
                if (wanted)
                {
                    Acquire();
                }
                else
                {
                    DetachLease(reason, true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Steam Input lease update failed.", ex);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    ///     Releases the shared lease and asks the gate to resume Steam's
    ///     controller discovery. Never throws because it runs during shutdown.
    ///     Unconditional: this is the recovery/shutdown form, so it drops every
    ///     recorded owner claim as well. Surface owners use <see cref="Drop" />.
    /// </summary>
    /// <param name="reason">Why the lease is released; logged for device diagnosis.</param>
    public static void ReleaseBestEffort(string reason)
    {
        Task pending;
        lock (Sync)
        {
            lock (OwnersSync)
            {
                Owners.Clear();
            }

            DetachLease(reason, false);
            pending = _nativeRelease;
        }

        // A surface release may still be recovering Steam's controllers. Letting the process
        // exit first would close its pipe without that recovery.
        try
        {
            if (!pending.Wait(PendingReleaseWait))
            {
                Log.Warn($"Steam Input surface release still running at {reason}; exiting without waiting further.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam Input surface release failed before {reason}: {ex.Message}");
        }
    }

    // The owner decision and the detach happen under Sync, so a surface that reopens (its
    // acquire is chained after this release) sees the lease gone and acquires a fresh one at
    // once. The native release can take several seconds when the payload lacks internal
    // recovery, because the host then rescans Steam; holding Sync across that made the next
    // surface open wait for it. A second lease acquired meanwhile keeps Steam blocked, since the
    // gate counts leases, so releasing the old one late is safe.
    private static void DetachLease(string reason, bool inBackground)
    {
        SteamInputBlockLease lease;
        lock (Sync)
        {
            if (_lease is null)
            {
                return;
            }

            lease = _lease;
            _lease = null;
            if (inBackground)
            {
                _nativeRelease = _nativeRelease.ContinueWith(
                    _ => ReleaseNative(lease, reason),
                    TaskScheduler.Default);
                return;
            }
        }

        ReleaseNative(lease, reason);
    }

    private static void ReleaseNative(SteamInputBlockLease lease, string reason)
    {
        try
        {
            // Release already performs recovery; repeating it here costs a
            // second multi-second scan of Steam's address space that the next
            // overlay open then waits on.
            var outcome = lease.Release();
            Log.Info($"Steam Input lease released ({reason}; {outcome.Status.LeaseCount} active " +
                     $"leases remain; recovery {DescribeRecovery(outcome)}).");
            if (outcome.RecoveryRequested)
            {
                return;
            }

            // Blocking is lifted — Steam keeps working, it just has not
            // been told to look for controllers again, so a pad can stay
            // missing in Steam until it notices by itself.
            Log.Warn($"Steam Input controller recovery did not run ({reason}): {outcome.RecoveryMessage}");
            RaiseRecoveryWarning();
        }
        catch (Exception ex)
        {
            // The SafeHandle/pipe lifetime makes a failed handshake crash-safe.
            lease.Dispose();
            Log.Error($"Steam Input lease release failed ({reason}).", ex);
        }
    }

    private static string DescribeRecovery(SteamInputReleaseOutcome outcome)
    {
        return outcome.Recovery switch
        {
            SteamControllerRecovery.Scheduled => "scheduled by the payload",
            SteamControllerRecovery.Completed => outcome.Rescan is { } rescan
                ? $"run by the host (scans {rescan.ScanCountBefore}→{rescan.ScanCountAfter})"
                : "run by the host",
            SteamControllerRecovery.NotRequired => "not required",
            _ => "UNAVAILABLE"
        };
    }

    /// <summary>
    ///     Probes host-side recovery at acquire time. Runs while Steam is
    ///     mid-teardown of its HID thread, so a failure is only a heads-up, not proof
    ///     recovery will fail: the release path performs the real recovery and is the
    ///     user-facing authority. Log it; never raise the panel warning off this probe.
    /// </summary>
    private static void CheckHostRecoveryBestEffort()
    {
        try
        {
            _client?.CheckRecovery();
            Log.Info("Steam Input host recovery probe succeeded.");
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Steam Input acquire-time host recovery probe failed (release-path recovery remains authoritative): {ex.Message}");
        }
    }

    private static void RaiseRecoveryWarning()
    {
        Log.Warn("Steam Input dynamic controller-recovery resolver is unavailable; GitHub report requested.");
        RecoveryWarningRaised?.Invoke(DynamicRecoveryWarning);
    }
}
