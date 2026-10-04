using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

/// <summary>A cycle service whose temporary changes the recovery journal restores on release.</summary>
internal abstract class ClawJournalledService(string serviceId) : DeviceService<ClawIdentityState>(serviceId)
{
    protected async ValueTask<DeviceServiceResult> RestoreJournalledAsync<TSnapshot>(
        DeviceCycleContext<ClawIdentityState> context,
        ClawRecoveryJournal journal,
        Func<ClawRecoveryState?, TSnapshot?> readSnapshot,
        Func<TSnapshot, CancellationToken, ValueTask<bool>> restoreAsync,
        string recoveryNoun,
        string budgetLabel,
        string unverifiedMessage,
        CancellationToken cancellationToken)
        where TSnapshot : class
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(DeviceServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not DeviceServiceState.Owned || !journal.HasUnrestoredMutation(ServiceId))
        {
            return Set(DeviceServiceState.Idle);
        }

        var restoreSnapshot = readSnapshot(journal.OriginalStateFor(ServiceId));
        if (restoreSnapshot is null)
        {
            return Set(DeviceServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The {recoveryNoun} recovery record did not contain its pre-mutation snapshot."));
        }

        ClawWriteBudget.Require(context.Deadline, budgetLabel);
        bool restored;
        try
        {
            restored = await restoreAsync(restoreSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await journal.SetStatusAsync(
                ServiceId,
                DeviceRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await journal.SetStatusAsync(
            ServiceId,
            restored ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(DeviceServiceState.Idle)
            : Set(DeviceServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                unverifiedMessage));
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

    /// <summary>True when neither the EC nor the BIOS version was known, so the binding cannot tell two apart.</summary>
    public static bool IsUnknownEc(string identity)
    {
        return identity.StartsWith("ec:unknown;", StringComparison.Ordinal);
    }

    /// <summary>
    ///     True for a power or fan binding, <c>ec:&lt;version&gt;</c> or <c>bios:&lt;version&gt;</c> then
    ///     <c>;msi-acpi:&lt;major.minor&gt;</c>, as <see cref="WindowsClawIdentityReader" /> builds it. The
    ///     reference unit's reads <c>ec:1T52EMS1.109;msi-acpi:8.0</c>, the value every earlier journal carries.
    /// </summary>
    public static bool IsWmi(string identity)
    {
        return (identity.StartsWith("ec:", StringComparison.Ordinal)
                || identity.StartsWith("bios:", StringComparison.Ordinal))
               && identity.Contains(";msi-acpi:", StringComparison.Ordinal);
    }
}
