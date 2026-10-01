// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Asus.RogAlly;

// Reconciling the recovery journal's outstanding entries at the start of a cycle.
public sealed partial class RogAllyPlugin
{
    private async ValueTask ReconcileOutstandingAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        if (_journal is null || _model is null)
        {
            return;
        }

        foreach (var entry in _journal.OutstandingEntries)
        {
            var current = entry.ServiceId == AllyServiceIds.Controller
                ? AllyServiceIds.McuFirmware
                : identity.FirmwareIdentity;
            var action = AllyRecoveryJournal.Decide(entry, current);
            var service = entry.ServiceId switch
            {
                AllyServiceIds.Power => (DeviceService<AllyIdentityState>?)_power,
                AllyServiceIds.Fans => _fans,
                AllyServiceIds.Controller => _controller,
                _ => null
            };
            if (action is AllyReconciliationAction.Block)
            {
                // Not retried and not blocking: the service stays usable, and the next explicit command
                // re-arms the captured original for the following release.
                PluginTrace.Warn("recovery",
                    $"'{entry.ServiceId}' kept an unresolved {entry.Status} restore; it is not retried automatically.");
                continue;
            }

            if (action is AllyReconciliationAction.ReportOnly)
            {
                Block(new CapabilityReason(CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore on this firmware."), service);
                continue;
            }

            bool restored;
            try
            {
                restored = await RestoreEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                PluginTrace.Failure("recovery", $"restoring '{entry.ServiceId}' failed", ex);
                restored = false;
            }

            await _journal.SetStatusAsync(entry.ServiceId,
                restored ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            PluginTrace.Info("recovery", $"outstanding '{entry.ServiceId}' restored={restored}.");
            if (!restored)
            {
                Block(new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."), service);
            }
        }
    }

    private async ValueTask<bool> RestoreEntryAsync(
        DeviceRecoveryEntry<AllyRecoveryState> entry,
        CancellationToken cancellationToken)
    {
        switch (entry.ServiceId)
        {
            case AllyServiceIds.Power when entry.OriginalState.ToPower() is { } power && _hardware.Acpi.TryOpen():
                return await new AllyPowerCapability(_hardware.Acpi, _model!, _hardware.Delay)
                    .RestoreAsync(power, cancellationToken)
                    .ConfigureAwait(false);
            case AllyServiceIds.Fans when entry.OriginalState.ToFans() is { } fans && _hardware.Acpi.TryOpen():
                var capability = new AllyFanCapability(_hardware.Acpi, _hardware.Delay);
                capability.Probe();
                return await capability.RestoreAsync(fans, cancellationToken).ConfigureAwait(false);
            case AllyServiceIds.Controller when _vendor is not null
                                                && await _vendor.IsAvailableAsync(cancellationToken)
                                                    .ConfigureAwait(false):
                foreach (var report in AllyProtocol.DefaultConfiguration)
                {
                    await _vendor.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
                }

                // Acknowledged is the most the MCU can say about its tables.
                return true;
            default:
                return false;
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
