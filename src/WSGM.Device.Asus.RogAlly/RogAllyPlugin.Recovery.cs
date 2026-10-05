// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Asus.RogAlly;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
public sealed partial class RogAllyPlugin
{
    /// <remarks>
    ///     No outcome here blocks a service. An entry whose transport is not there yet waits, one from another
    ///     BIOS is dropped, and any other is written once: a restore that dispatched removes it, and one that
    ///     failed to dispatch leaves it pending for the next release or start, with no status write.
    /// </remarks>
    private async ValueTask ReconcileOutstandingAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (_journal is null || _model is null)
        {
            return;
        }

        foreach (var entry in _journal.OutstandingEntries)
        {
            // Earlier builds read restores back and recorded the result. A restore that reached the
            // device is complete now (D9), and one that failed is pending again, so this start writes it.
            if (entry.Status is DeviceRecoveryStatus.RestoredUnverified)
            {
                PluginTrace.Info("recovery", $"'{entry.ServiceId}' was restored by an earlier build; entry removed.");
                await _journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoredVerified,
                    CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (entry.Status is DeviceRecoveryStatus.RestoreFailed)
            {
                PluginTrace.Info("recovery", $"'{entry.ServiceId}' kept a failed restore from an earlier build; "
                                             + "it is pending again.");
                await _journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.Pending,
                    CancellationToken.None).ConfigureAwait(false);
            }

            string? current;
            if (entry.ServiceId == AllyServiceIds.Controller)
            {
                current = _vendor is not null && await _vendor.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
                    ? AllyServiceIds.McuFirmware
                    : null;
            }
            else
            {
                current = _hardware.Acpi.TryOpen() ? identity.FirmwareIdentity : null;
            }

            switch (AllyRecoveryJournal.Decide(entry, current))
            {
                case AllyReconciliationAction.Wait:
                    PluginTrace.Info("recovery", $"'{entry.ServiceId}' waits: its transport is not available yet.");
                    continue;
                case AllyReconciliationAction.Discard:
                    PluginTrace.Warn("recovery",
                        $"dropped the '{entry.ServiceId}' entry bound to {entry.FirmwareIdentity}: the firmware now "
                        + $"reads {current}, so its captured state is not restored.");
                    await _journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoredVerified,
                        CancellationToken.None).ConfigureAwait(false);
                    continue;
                case AllyReconciliationAction.Restore:
                default:
                    break;
            }

            try
            {
                await RestoreEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                PluginTrace.Failure("recovery", $"restoring '{entry.ServiceId}' failed; the entry stays pending", ex);
                continue;
            }

            await _journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoredVerified,
                CancellationToken.None).ConfigureAwait(false);
            PluginTrace.Info("recovery", $"outstanding '{entry.ServiceId}' restored.");
        }
    }

    /// <summary>Writes an entry's original, or throws when a write failed to dispatch.</summary>
    private async ValueTask RestoreEntryAsync(
        DeviceRecoveryEntry<AllyRecoveryState> entry,
        CancellationToken cancellationToken)
    {
        switch (entry.ServiceId)
        {
            case AllyServiceIds.Power when entry.OriginalState.ToPower() is { } power:
                _ = await new AllyPowerCapability(_hardware.Acpi, _model!, _hardware.Delay)
                    .RestoreAsync(power, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case AllyServiceIds.Fans when entry.OriginalState.ToFans() is { } fans:
                _ = await new AllyFanCapability(_hardware.Acpi, _hardware.Delay)
                    .RestoreAsync(fans, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case AllyServiceIds.Controller:
                foreach (var report in AllyProtocol.DefaultConfiguration)
                {
                    // Acknowledged is the most the MCU can say about its tables; a refused one throws and
                    // leaves the entry pending.
                    await _vendor!.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
                }

                return;
        }
    }

    private static void Block(CapabilityReason reason, params DeviceService<AllyIdentityState>?[] services)
    {
        foreach (var service in services)
        {
            service?.ReconciliationBlockReason = reason;
        }
    }
}
