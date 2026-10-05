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

// What the periodic and post-command refresh re-reads; DeviceCommandSerializer runs both.
public sealed partial class RogAllyPlugin
{
    /// <summary>Re-reads what one capability, or every capability when the id is null, observes.</summary>
    /// <remarks>The ACPI reads are synchronous, so there is nothing for the token to cancel.</remarks>
    private ValueTask RefreshAsync(string? capabilityId, CancellationToken cancellationToken)
    {
        try
        {
            if (_power is { State: DeviceServiceState.Owned } && capabilityId is null
                    or CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario)
            {
                _power.Refresh();
            }

            if (_fans is { State: DeviceServiceState.Owned } && capabilityId is null
                    or CapabilityIds.FanCurve or CapabilityIds.FanMode or CapabilityIds.Scenario)
            {
                _fans.Refresh();
            }

            if (_charge is { State: DeviceServiceState.Owned } && capabilityId is null or CapabilityIds.ChargeLimit)
            {
                _charge.Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            // A stale reading is modelled by WSGM's freshness policy; the loop keeps observing.
            PluginTrace.Change("observe", "acpi", DiagnosticText.FromException("ATKACPI read failed", ex));
        }

        return ValueTask.CompletedTask;
    }
}
