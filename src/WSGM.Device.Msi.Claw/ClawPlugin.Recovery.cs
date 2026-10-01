using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Msi.Claw;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
public sealed partial class ClawPlugin
{
    private async ValueTask ReconcileOutstandingAsync(
        IReadOnlyList<DeviceRecoveryEntry<ClawRecoveryState>> entries,
        ClawIdentityState identity,
        ClawPowerCapability powerCapability,
        ClawFanCapability fanCapability,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (ServiceFor(entry.ServiceId)?.ReconciliationBlockReason is not null)
            {
                continue;
            }

            var currentFirmware = entry.ServiceId switch
            {
                ServiceIds.Power or ServiceIds.Fans when identity.WmiAvailable =>
                    identity.WmiFirmwareIdentity,
                ServiceIds.Controller when identity.ExactMachineMatch =>
                    ClawFirmwareIdentities.Mcu,
                _ => null
            };
            var action = ClawRecoveryJournal.Decide(entry, currentFirmware);
            if (action is ClawReconciliationAction.Discard)
            {
                PluginTrace.Warn(
                    "recovery",
                    $"dropped the {entry.ServiceId} entry bound to {entry.FirmwareIdentity}: the firmware now reads "
                    + $"{currentFirmware}, so its captured state is not restored.");
                await _journal.SetStatusAsync(
                    entry.ServiceId,
                    DeviceRecoveryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (action is not ClawReconciliationAction.Restore)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    action is ClawReconciliationAction.Block
                        ? CapabilityReasonCode.TransportFaulted
                        : CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore automatically."));
                continue;
            }

            bool restored;
            var restoreFailed = false;
            try
            {
                restored = entry.ServiceId switch
                {
                    ServiceIds.Power when ClawRecoveryValues.TryPower(
                            entry.OriginalState,
                            out var power) =>
                        await powerCapability.RestoreAsync(power!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Fans when ClawRecoveryValues.TryFans(
                            entry.OriginalState,
                            out var fans) =>
                        await fanCapability.RestoreAsync(fans!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Controller =>
                        await RestoreControllerJournalEntryAsync(entry, cancellationToken)
                            .ConfigureAwait(false),
                    _ => false
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                restored = false;
                restoreFailed = true;
                await _journal.SetStatusAsync(
                    entry.ServiceId,
                    DeviceRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (restored)
            {
                await _journal.SetStatusAsync(
                    entry.ServiceId,
                    DeviceRecoveryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!restoreFailed)
            {
                await _journal.SetStatusAsync(
                    entry.ServiceId,
                    DeviceRecoveryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!restored)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."));
            }
        }
    }

    private async ValueTask<bool> RestoreControllerJournalEntryAsync(
        DeviceRecoveryEntry<ClawRecoveryState> entry,
        CancellationToken cancellationToken)
    {
        if (!ClawRecoveryValues.TryControllerMode(entry.OriginalState, out var mode))
        {
            return false;
        }

        var current = await _services.Controller.DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || string.IsNullOrWhiteSpace(current.PhysicalLocation))
        {
            return false;
        }

        if (current.Mode == mode)
        {
            return true;
        }

        var deadline = Deadline.After(TimeSpan.FromSeconds(6));
        var restored = await _services.Mcu.SwitchModeAsync(
            mode,
            current.PhysicalLocation,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return restored.Mode == mode
               && HidDevices.SamePhysicalLocation(
                   restored.PhysicalLocation,
                   current.PhysicalLocation);
    }

    private void BlockService(string serviceId, CapabilityReason reason)
    {
        var service = ServiceFor(serviceId);
        service?.ReconciliationBlockReason = reason;
    }

    private DeviceServiceStatus? ServiceFor(string serviceId)
    {
        return serviceId switch
        {
            ServiceIds.Power => _power,
            ServiceIds.Fans => _fans,
            ServiceIds.Controller => _controller,
            _ => null
        };
    }
}
