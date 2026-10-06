using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

/// <summary>A cycle service whose temporary changes the recovery journal restores on release.</summary>
internal abstract class ClawJournalledService(string serviceId) : DeviceService<ClawIdentityState>(serviceId)
{
    /// <summary>Writes a pending original back. A restore is complete once its writes went through.</summary>
    /// <remarks>
    ///     Only a pending entry is written: one an earlier restore left unverified or failed waits for an
    ///     explicit command. A release without the time to write leaves its entry pending for the next
    ///     start, and a restore that throws records the failure so nothing writes it again automatically.
    /// </remarks>
    protected async ValueTask<DeviceServiceResult> RestoreJournalledAsync<TSnapshot>(
        DeviceCycleContext<ClawIdentityState> context,
        ClawRecoveryJournal journal,
        Func<ClawRecoveryState?, TSnapshot?> readSnapshot,
        Func<TSnapshot, CancellationToken, ValueTask> restoreAsync,
        CancellationToken cancellationToken)
        where TSnapshot : class
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(DeviceServiceState.Faulted, ReconciliationBlockReason);
        }

        // Only on the BIOS the original was captured under: an entry a start could not compare waits.
        if (State is not DeviceServiceState.Owned
            || journal.EntryFor(ServiceId) is not { Status: DeviceRecoveryStatus.Pending } entry
            || !string.Equals(entry.FirmwareIdentity, context.Identity.RecoveryBinding, StringComparison.Ordinal)
            || readSnapshot(entry.OriginalState) is not { } restoreSnapshot)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (!DeviceWriteBudget.IsAvailable(context.Deadline))
        {
            return Set(DeviceServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Not enough time to restore; the original stays recorded for the next start."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await restoreAsync(restoreSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await journal.SetStatusAsync(
                ServiceId,
                DeviceRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
            .ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }
}

/// <summary>A first read that may be refused without keeping a service from starting.</summary>
internal static class ClawObservation
{
    public static async ValueTask<T?> TryAsync<T>(
        Func<CancellationToken, ValueTask<T>> read,
        string scope,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            PluginTrace.Failure(scope, "The initial read failed; the value stays unknown until a write", ex);
            return null;
        }
    }
}

internal static class ServiceIds
{
    public const string OemEvents = "msi-oem-events";
    public const string Power = "msi-power";
    public const string ChargeLimit = "msi-charge-limit";
    public const string Fans = "msi-fans";
    public const string Telemetry = "msi-telemetry";
    public const string Lighting = "claw-lighting";
    public const string Motion = "claw-motion";
    public const string Controller = "physical-controller";
    public const string ChordSuppressor = "firmware-chord-suppressor";
}

internal static class ClawFirmwareIdentities
{
    /// <summary>
    ///     Deliberately carries no revision. A controller journal entry only records the mode to put
    ///     back, and that write is valid on any MCU firmware the exact machine ships with; a
    ///     revision here would strand the entry, and with it the controller, after every firmware
    ///     update.
    /// </summary>
    public const string Mcu = "mcu";

    /// <summary>The power and fan binding for a BIOS version, <c>bios:&lt;version&gt;</c>.</summary>
    public static string Bios(string biosVersion)
    {
        return "bios:" + biosVersion;
    }

    /// <summary>True for a power or fan binding as this build writes it: <c>bios:&lt;version&gt;</c>.</summary>
    public static bool IsBios(string identity)
    {
        return identity.StartsWith("bios:", StringComparison.Ordinal) && !IsLegacy(identity);
    }

    /// <summary>
    ///     True for a power or fan binding an earlier build wrote: <c>ec:&lt;version&gt;</c> or
    ///     <c>bios:&lt;version&gt;</c> then <c>;msi-acpi:&lt;major.minor&gt;</c>. The reference unit's reads
    ///     <c>ec:1T52EMS1.109;msi-acpi:8.0</c>. Such an entry is restored once when this start reads the same
    ///     binding, and discarded otherwise.
    /// </summary>
    public static bool IsLegacy(string identity)
    {
        return (identity.StartsWith("ec:", StringComparison.Ordinal)
                || identity.StartsWith("bios:", StringComparison.Ordinal))
               && identity.Contains(";msi-acpi:", StringComparison.Ordinal);
    }

    /// <summary>True for a legacy binding without an EC or BIOS version, which cannot tell two firmwares apart.</summary>
    public static bool IsUnknownEc(string identity)
    {
        return identity.StartsWith("ec:unknown;", StringComparison.Ordinal);
    }
}
