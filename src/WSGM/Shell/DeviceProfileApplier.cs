using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What happened when an authored profile was applied.</summary>
public enum DeviceProfileApplyOutcome
{
    /// <summary>The profile was sent to the device.</summary>
    Applied,

    /// <summary>No profile is selected for this capability; nothing was changed.</summary>
    NoSelection,

    /// <summary>A profile is selected but cannot be applied to the device as it is now.</summary>
    Refused,

    /// <summary>The device accepted the command but reported failure.</summary>
    Failed,
}

/// <summary>
/// Applies the authored profile in force for the running application to the device.
/// </summary>
/// <remarks>
/// Three steps, and each can stop the chain for a different reason worth logging separately:
/// resolve which profile the selection points at, check it against the descriptor the device
/// publishes right now, and only then send it.
/// <para>
/// The middle step is not optional. Profiles are authored in Settings with no plugin running, so a
/// curve is built against the last known bounds; between authoring and applying, the device can be
/// updated, swapped, or downgraded. Sending an unchecked curve means the plugin refuses it and the
/// user sees a profile that silently does nothing.
/// </para>
/// </remarks>
internal sealed class DeviceProfileApplier
{
    private readonly Func<string, CapabilityDescriptor?> _describe;
    private readonly Func<string, CapabilityValue, CancellationToken, Task<bool>> _execute;

    /// <summary>Creates an applier.</summary>
    /// <param name="describe">Reads the descriptor the device publishes for a capability.</param>
    /// <param name="execute">Sends a value to the device, reporting whether it took.</param>
    internal DeviceProfileApplier(
        Func<string, CapabilityDescriptor?> describe,
        Func<string, CapabilityValue, CancellationToken, Task<bool>> execute)
    {
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Applies the profile in force for one capability.</summary>
    /// <param name="selections">Selections stored for the device.</param>
    /// <param name="profiles">Profiles authored for the device.</param>
    /// <param name="capabilityId">The capability to apply.</param>
    /// <param name="applicationId">The running application identity, or null for none.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>What happened, for the caller to act on and for the log.</returns>
    internal async Task<DeviceProfileApplyOutcome> ApplyAsync(
        IReadOnlyList<DeviceProfileSelection> selections,
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string capabilityId,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionResolver.Resolve(
            selections,
            profiles,
            capabilityId,
            applicationId);

        if (resolution.Profile is not { } profile)
        {
            // A dangling reference and no selection at all are different facts. The first is a
            // mistake the user can fix once they know; the second is the normal state.
            if (resolution.Diagnostic is { } diagnostic)
            {
                Log.Warn($"Device profile for '{capabilityId}' not applied: {diagnostic}.");
                return DeviceProfileApplyOutcome.Refused;
            }

            Log.Change(
                $"device-profile/{capabilityId}",
                $"Device profile for '{capabilityId}': no selection is in force.");
            return DeviceProfileApplyOutcome.NoSelection;
        }

        CapabilityDescriptor? descriptor = _describe(capabilityId);
        DeviceProfileRejection rejection = DeviceProfileValidation.Validate(
            profile,
            descriptor,
            out string? reason);
        if (rejection is not DeviceProfileRejection.None)
        {
            Log.Warn(
                $"Device profile '{profile.ProfileId}' refused for '{capabilityId}' "
                + $"({rejection}): {reason}.");
            return DeviceProfileApplyOutcome.Refused;
        }

        CapabilityValue value = new()
        {
            Kind = CapabilityValueKind.Curve,
            CurveValue =
            [
                .. profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)),
            ],
        };

        bool applied = await _execute(capabilityId, value, cancellationToken).ConfigureAwait(false);
        if (!applied)
        {
            Log.Warn(
                $"Device profile '{profile.ProfileId}' was accepted for '{capabilityId}' but the "
                + "device reported failure.");
            return DeviceProfileApplyOutcome.Failed;
        }

        Log.Info(
            $"Device profile '{profile.ProfileId}' applied to '{capabilityId}' "
            + (resolution.ApplicationScoped
                ? $"for application '{applicationId}'."
                : "as the global selection."));
        return DeviceProfileApplyOutcome.Applied;
    }
}
