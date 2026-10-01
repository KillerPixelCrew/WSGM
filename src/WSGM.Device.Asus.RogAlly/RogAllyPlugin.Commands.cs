// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Asus.RogAlly;

// Command validation and dispatch to the capabilities.
public sealed partial class RogAllyPlugin
{
    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            candidate.CapabilityId == command.CapabilityId && candidate.InstanceId == command.InstanceId);
        var service = ServiceFor(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                $"Capability '{command.CapabilityId}' is not available.");
        }

        AllyIdentityState identity;
        try
        {
            identity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted,
                $"Identity revalidation failed: {ex.GetType().Name}.");
        }

        var refusal = Refusal(service, identity) ?? Validate(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return CommandResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (AllyBudgetException ex)
        {
            // Thrown before any write, so nothing needs restoring and the service stays healthy.
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, ex.Message, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Indeterminate(command, CapabilityReasonCode.Quiescing,
                "The command was cancelled after hardware application began.", RollbackResult.RestoreFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException("The capability handler failed after admission", ex),
                RollbackResult.RestoreFailed);
        }

        // A blind write with no journalled original has nothing to reconcile, so it does not fault.
        if (result.Rollback is RollbackResult.RestoreFailed
            && service is PowerService { HasJournalledOriginal: true } or FanService { HasJournalledOriginal: true })
        {
            service.Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "A command rollback failed; the resource stays faulted until it is acquired again, and stop "
                + "restores the journalled original."));
        }

        if (result.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified
            && command.RequestedValue is { } requested)
        {
            // What was written stands in for a readback the firmware cannot give (SDK: an unverified
            // write earns Observed at best). It lives only for this cycle.
            _written[(command.CapabilityId, command.InstanceId)] = requested;
        }

        return result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null
            ? result with { ReadbackValue = null }
            : result;
    }

    private async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        AllyIdentityState identity,
        CancellationToken cancellationToken)
    {
        var value = command.RequestedValue!;
        switch (command.CapabilityId)
        {
            case CapabilityIds.PowerSustained or CapabilityIds.PowerBoost:
                if (!DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained, out var boost,
                        out _))
                {
                    throw new InvalidOperationException("A power limit command was admitted without a valid pair.");
                }

                AllyWriteBudget.Require(command.Deadline, "power limit");
                await _power!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false);

                return await _power.Capability!.ApplyLimitsAsync(command, sustained, boost, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.Scenario:
                AllyWriteBudget.Require(command.Deadline, "performance mode");
                await _power!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false);

                return await _power.Capability!.ApplyScenarioAsync(command, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.ChargeLimit:
                return _charge!.Capability!.Apply(command, value.IntegerValue!.Value);
            case CapabilityIds.FanCurve:
                AllyWriteBudget.Require(command.Deadline, "fan curve");
                await _fans!.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false);

                return await _fans.Capability!.ApplyCurveAsync(command, value.CurveValue, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanMode:
                return await ApplyFanModeAsync(command, identity, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingBrightness:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Brightness = value.IntegerValue!.Value }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingSpeed:
                return await _lighting!.ApplyAsync(command,
                    state => state with { Speed = value.IntegerValue!.Value }, cancellationToken).ConfigureAwait(false);
            case CapabilityIds.LightingEffect:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Effect = Effects.Parse(value.ChoiceValue!) }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingColor:
                return await _lighting!.ApplyAsync(command, state => command.InstanceId == CapabilityInstances.Left
                    ? state with { LeftColor = value.ColorValue!.Value }
                    : state with { RightColor = value.ColorValue!.Value }, cancellationToken).ConfigureAwait(false);
            default:
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "This capability is read-only.");
        }
    }

    private async ValueTask<CapabilityCommandResult> ApplyFanModeAsync(
        CapabilityCommand command,
        AllyIdentityState identity,
        string mode,
        CancellationToken cancellationToken)
    {
        var fans = _fans!;
        if (mode == FanModes.Automatic)
        {
            AllyWriteBudget.Require(command.Deadline, "fan mode");
            await fans.PrepareWriteAsync(identity, cancellationToken).ConfigureAwait(false);

            return await fans.Capability!.ApplyAutomaticAsync(command, fans.Original, cancellationToken)
                .ConfigureAwait(false);
        }

        // There is no custom-mode firmware switch. The next curve command changes the fans.
        return CommandResults.Unverified(command, "Custom mode is ready; send a fan curve to change the fans.");
    }

    private DeviceService<AllyIdentityState>? ServiceFor(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _charge,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanReading => _fans,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingEffect or CapabilityIds.LightingSpeed
                or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private static CapabilityReason? Refusal(DeviceService<AllyIdentityState> service, AllyIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                "The live identity no longer matches the Ally model.", true);
        }

        return service.State switch
        {
            DeviceServiceState.Owned => null,
            DeviceServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                service.Reason?.Detail ?? "The device service is unavailable."),
            DeviceServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            DeviceServiceState.Degraded or DeviceServiceState.Faulted or DeviceServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    service.Reason?.Detail ?? "The device service is faulted."),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? Validate(CapabilityCommand command, CapabilityDescriptor descriptor, bool onAcPower)
    {
        if (_descriptorSet is null || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
                                   || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                "The command targets a descriptor or device generation that is no longer current.", true);
        }

        if (!AllyWriteBudget.IsAvailable(command.Deadline))
        {
            return new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The command deadline leaves too little time for a hardware write.", true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(CapabilityReasonCode.UnavailableOnPowerSource,
                "The capability is unavailable on this power source.");
        }

        if (command.RequestedValue is not { } value)
        {
            return descriptor.SupportsAction
                ? new CapabilityReason(CapabilityReasonCode.Unsupported, "Rumble is driven through haptic frames.")
                : new CapabilityReason(CapabilityReasonCode.Unsupported, "The capability is not an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "The capability is read-only.");
        }

        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match {descriptor.ValueKind}.");
        }

        var invalid = descriptor.ValueKind switch
        {
            CapabilityValueKind.Integer when value.IntegerValue is not { } integer
                                             || integer < descriptor.Minimum || integer > descriptor.Maximum =>
                OutOfRange("The value is outside the declared range."),
            CapabilityValueKind.Choice when descriptor.Choices.All(choice => choice.Value != value.ChoiceValue) =>
                OutOfRange("The choice is not one of the declared options."),
            CapabilityValueKind.Color when value.ColorValue is not (>= 0 and <= 0xFFFFFF) =>
                OutOfRange("The colour must be 24-bit RGB."),
            CapabilityValueKind.Curve when value.CurveValue.Count == 0 => OutOfRange("The curve has no points."),
            _ => null
        };
        if (invalid is not null)
        {
            return invalid;
        }

        // WSGM decides both limits of the pair; a command that does not carry a valid one is refused.
        return DevicePowerPair.Peer(_descriptorSet.Descriptors, command.CapabilityId) is not null
               && !DevicePowerPair.TryResolve(command, _descriptorSet.Descriptors, out _, out _, out var pairError)
            ? OutOfRange(pairError!)
            : null;
    }

    private static CapabilityReason OutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }
}
