using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

// Command admission, dispatch to the capabilities and the journalled write.
public sealed partial class ClawPlugin
{
    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, command.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, command.InstanceId, StringComparison.Ordinal));
        var service = ServiceForCapability(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                $"Capability '{CapabilityKey(command.CapabilityId, command.InstanceId)}' is not available.");
        }

        ClawIdentityState identity;
        try
        {
            identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (identity.ExactMachineMatch && identity.Model != _cycleModel)
            {
                // Refused before RefreshObservedAsync, so a changed model gets no hardware read either.
                return CommandResults.Rejected(command, new CapabilityReason(
                    CapabilityReasonCode.GenerationChanged,
                    "The Claw model no longer matches the one this cycle started on.",
                    true));
            }

            if (service.State is DeviceServiceState.Owned
                && identity.ExactMachineMatch
                && FirmwareVerified(identity, FirmwareForCapability(command.CapabilityId)))
            {
                await RefreshObservedAsync(command.CapabilityId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "Command was cancelled before hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"Current-state revalidation failed: {ex.GetType().Name}.");
        }

        var refusal = RefusalFor(
            service,
            FirmwareForCapability(command.CapabilityId),
            identity);
        refusal ??= ValidateCommand(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return CommandResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyCapabilityCommandAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(
                command,
                "Command was cancelled after hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Indeterminate(
                command,
                $"Capability handler failed after admission: {ex.GetType().Name}.");
        }

        return NormalizeCommandResult(command, result);
    }

    private ValueTask<CapabilityCommandResult> ApplyCapabilityCommandAsync(
        CapabilityCommand command,
        ClawIdentityState identity,
        CancellationToken cancellationToken)
    {
        // Admission refuses a WMI capability without the provider, and the provider's availability is
        // the binding itself, so only a WMI-free capability, which never journals, finds none.
        string WmiFirmware()
        {
            return identity.WmiFirmwareIdentity
                   ?? throw new InvalidOperationException("A WMI capability was admitted without a firmware binding.");
        }

        (int Sustained, int Boost) Limits()
        {
            return DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained, out var boost,
                out _)
                ? (sustained, boost)
                : throw new InvalidOperationException("A power limit command was admitted without a valid pair.");
        }

        var power = _powerCapability
                    ?? throw new InvalidOperationException("The power capability is unavailable.");
        var chargeLimit = _chargeLimitCapability
                          ?? throw new InvalidOperationException("The charge-limit capability is unavailable.");
        var fans = _fanCapability
                   ?? throw new InvalidOperationException("The fan capability is unavailable.");
        var lighting = _lightingCapability
                       ?? throw new InvalidOperationException("The lighting capability is unavailable.");

        return command.CapabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost => JournalCommandAsync(
                ServiceIds.Power,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) =>
                {
                    var (sustained, boost) = Limits();
                    return power.ApplyLimitsAsync(journalCommand, sustained, boost, token);
                },
                cancellationToken),
            CapabilityIds.Scenario => JournalCommandAsync(
                ServiceIds.Power,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyScenarioAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.ChargeLimit => chargeLimit.ApplyAsync(
                command,
                command.RequestedValue!.IntegerValue!.Value,
                cancellationToken),
            CapabilityIds.FanMode => JournalCommandAsync(
                ServiceIds.Fans,
                WmiFirmware(),
                command,
                async token => ClawRecoveryValues.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => fans.ApplyModeAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.FanCurve => ApplyFanCurveCommandAsync(command, fans, WmiFirmware(), cancellationToken),
            CapabilityIds.LightingBrightness => lighting.ApplyAsync(
                command,
                state => state with
                {
                    Brightness = command.RequestedValue!.IntegerValue!.Value
                },
                cancellationToken),
            CapabilityIds.LightingColor => lighting.ApplyAsync(
                command,
                state => command.InstanceId switch
                {
                    CapabilityInstances.RightRing => state with
                    {
                        RightRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.LeftRing => state with
                    {
                        LeftRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.Buttons => state with
                    {
                        ButtonsColor = command.RequestedValue!.ColorValue!.Value
                    },
                    _ => state
                },
                cancellationToken),
            CapabilityIds.VariableRefreshRate => ApplyVariableRefreshCommand(command),
            CapabilityIds.EnduranceGaming or CapabilityIds.EnduranceGamingMode =>
                ApplyEnduranceGamingCommand(command),
            CapabilityIds.ShaderDownload => ApplyShaderDownloadCommand(command),
            CapabilityIds.SharedGpuMemory => ApplySharedGpuMemoryCommand(command),
            CapabilityIds.DriverVsync => ApplyDriverVsyncCommand(command),
            _ => ReadOnlyHandler(command, cancellationToken)
        };
    }

    /// <remarks>
    ///     Not journalled, unlike the WMI and MCU writes. The journal exists so a value written into
    ///     firmware can be put back after an abnormal exit; this one is held by the graphics driver,
    ///     restored from the profile captured at cycle start, and reported as verified only because the
    ///     read-back agrees rather than because the call returned success.
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplyVariableRefreshCommand(CapabilityCommand command)
    {
        if (_arcSync is not { IsAvailable: true } display)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "No variable-refresh capable panel is present."));
        }

        var requested = command.RequestedValue!.BooleanValue!.Value;
        if (!display.TryWrite(requested))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The display driver did not apply variable refresh {(requested ? "on" : "off")}."));
        }

        // Verified rather than unverified: TryWrite only reports success once the profile has been
        // read back and agrees, so the readback carried here is an observation and not a hope.
        return ValueTask.FromResult(CommandResults.Verified(command, CapabilityValue.Boolean(requested)));
    }

    /// <remarks>
    ///     The driver holds control and target together, so either row's write has to carry the other's
    ///     current value rather than a remembered one. Not journalled, for the same reason variable
    ///     refresh is not: nothing was written into firmware, and Restore puts back what was captured
    ///     when the cycle started.
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplyEnduranceGamingCommand(CapabilityCommand command)
    {
        if (_arcSync is not { IsEnduranceGamingAvailable: true } display
            || display.ReadEnduranceGaming() is not { } current)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "The graphics driver does not offer Endurance Gaming on this device."));
        }

        var requested = command.RequestedValue!.ChoiceValue!;
        var control = current.Control;
        var mode = current.Mode;
        if (command.CapabilityId == CapabilityIds.EnduranceGaming)
        {
            control = requested switch
            {
                "on" => EnduranceGamingControl.On,
                "auto" => EnduranceGamingControl.Auto,
                _ => EnduranceGamingControl.Off
            };
        }
        else
        {
            mode = requested switch
            {
                "balanced" => EnduranceGamingMode.Balanced,
                "battery" => EnduranceGamingMode.Battery,
                _ => EnduranceGamingMode.Performance
            };
        }

        if (!display.TryWriteEnduranceGaming(control, mode))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The graphics driver did not apply Endurance Gaming {control}/{mode}."));
        }

        // Verified rather than unverified: the transport reports success only after reading both
        // fields back and finding them equal to what was asked for.
        return ValueTask.FromResult(CommandResults.Verified(command, CapabilityValue.Choice(requested)));
    }

    /// <remarks>
    ///     Not journalled, for the same reason the other driver-held rows are not: nothing is written
    ///     into firmware, and Restore puts back what was captured when the cycle started.
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplyShaderDownloadCommand(CapabilityCommand command)
    {
        if (_arcSync is not { IsShaderDownloadAvailable: true } display)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "The graphics driver does not offer prebuilt shader download on this device."));
        }

        var requested = command.RequestedValue!.BooleanValue!.Value;
        if (!display.TryWriteShaderDownload(requested))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The graphics driver did not apply shader download {(requested ? "on" : "off")}."));
        }

        return ValueTask.FromResult(CommandResults.Verified(command, CapabilityValue.Boolean(requested)));
    }

    /// <remarks>
    ///     Not journalled, and deliberately not restored either. The journal is for firmware values that
    ///     have to be put back after an abnormal exit; this one is a persistent user choice the driver
    ///     keeps, in the same class as the charge limit.
    ///     <para>
    ///         The write is verified, the split is not: the driver reads this when it initializes, so the
    ///         memory the adapter reports only follows at the next restart. Reporting
    ///         <see cref="CommandOutcome.AppliedVerified" /> is still honest because the capability's value
    ///         is the requested percentage, and that is exactly what was read back.
    ///     </para>
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplySharedGpuMemoryCommand(CapabilityCommand command)
    {
        if (_arcSync is not { IsSharedGpuMemoryAvailable: true } display)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "The graphics driver does not offer a shared memory split on this device."));
        }

        var requested = command.RequestedValue!.IntegerValue!.Value;
        if (requested is < IntelGraphicsMemoryTransport.MinimumPercent
            or > IntelGraphicsMemoryTransport.MaximumPercent)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                $"The GPU memory share must be {IntelGraphicsMemoryTransport.MinimumPercent}-"
                + $"{IntelGraphicsMemoryTransport.MaximumPercent} percent."));
        }

        if (!display.TryWriteSharedGpuMemory(requested))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The graphics driver did not store a GPU memory share of {requested} percent."));
        }

        return ValueTask.FromResult(CommandResults.Verified(command, CapabilityValue.Integer(requested)));
    }

    /// <remarks>
    ///     Written to the driver's own 3D settings store rather than through IGCL. Measured on the
    ///     reference unit: <c>ctlGetSet3DFeature</c> reports success for a feature-9 write and changes
    ///     nothing observable (not its own getter, not the stored value), whether the caller is
    ///     elevated or not and with Intel Graphics Software running. The stored value does move, and it
    ///     carries Intel's own flag values, so that is where this reads and writes.
    ///     <para>
    ///         Not journalled and not restored: like the shared-memory split, this is a persistent user
    ///         choice the driver keeps, not a resource the plugin borrowed.
    ///     </para>
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplyDriverVsyncCommand(CapabilityCommand command)
    {
        if (_arcSync is not { } display || FlipModeChoices() is not { Length: > 1 } choices)
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "The graphics driver does not offer frame presentation modes on this device."));
        }

        var requested = command.RequestedValue!.ChoiceValue ?? "";
        if (!choices.Contains(requested))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                $"The driver does not offer the '{requested}' frame presentation mode."));
        }

        var bit = Array.Find(FlipModes, mode => mode.Value == requested).Bit;
        if (!display.TryWriteFlipMode(bit))
        {
            return ValueTask.FromResult(CommandResults.Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The graphics driver did not store the '{requested}' frame presentation mode."));
        }

        return ValueTask.FromResult(CommandResults.Verified(command, CapabilityValue.Choice(requested)));
    }

    private ValueTask<CapabilityCommandResult> ApplyFanCurveCommandAsync(
        CapabilityCommand command,
        ClawFanCapability fans,
        string wmiFirmware,
        CancellationToken cancellationToken)
    {
        return JournalCommandAsync(
            ServiceIds.Fans,
            wmiFirmware,
            command,
            async token => ClawRecoveryValues.Fans(
                await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
            (journalCommand, token) => fans.ApplyCurveAsync(
                journalCommand,
                journalCommand.RequestedValue!.CurveValue,
                token),
            cancellationToken);
    }

    private DeviceServiceStatus? ServiceForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve => _fans,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            CapabilityIds.VariableRefreshRate
                or CapabilityIds.EnduranceGaming
                or CapabilityIds.EnduranceGamingMode
                or CapabilityIds.ShaderDownload
                or CapabilityIds.SharedGpuMemory
                or CapabilityIds.DriverVsync => _arcSync,
            _ => null
        };
    }

    private static FirmwareKind FirmwareForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            // MCU-backed capabilities carry no revision gate. The controller mode switch is not an
            // addressed write, and lighting takes its address from HC's firmware table and, like HC,
            // writes without first confirming what the MCU holds.
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
                or CapabilityIds.Controller or CapabilityIds.Rumble => FirmwareKind.None,
            // Driven by the GPU driver, not by MSI firmware, so there is no firmware revision to gate
            // it on and gating it on the WMI one would refuse it whenever that path is degraded.
            CapabilityIds.Motion
                or CapabilityIds.VariableRefreshRate
                or CapabilityIds.EnduranceGaming
                or CapabilityIds.EnduranceGamingMode
                or CapabilityIds.ShaderDownload
                or CapabilityIds.SharedGpuMemory
                or CapabilityIds.DriverVsync => FirmwareKind.None,
            _ => FirmwareKind.Wmi
        };
    }

    private static CapabilityReason? RefusalFor(
        DeviceServiceStatus service,
        FirmwareKind firmwareKind,
        ClawIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation.",
                true);
        }

        if (!FirmwareVerified(identity, firmwareKind))
        {
            return new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Current firmware is outside the Claw implementation's verified gate.");
        }

        return service.State switch
        {
            DeviceServiceState.Owned => null,
            DeviceServiceState.Passive => new CapabilityReason(
                CapabilityReasonCode.ResourceConflict,
                "The device service is passive or held by another owner.", true),
            DeviceServiceState.Releasing => new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            DeviceServiceState.Degraded => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is degraded and cannot accept commands."),
            DeviceServiceState.Faulted or DeviceServiceState.ReleasedUnverified => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is faulted or its release could not be verified."),
            _ => new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? ValidateCommand(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        bool onAcPower)
    {
        if (_descriptorSet is null
            || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
            || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Command targets a descriptor or device generation that is no longer current.",
                true);
        }

        if (command.Deadline.HasExpired)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Command deadline passed before it could be applied.",
                true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(
                CapabilityReasonCode.UnavailableOnPowerSource,
                onAcPower
                    ? "Capability is unavailable on AC power."
                    : "Capability is unavailable on battery power.");
        }

        if (command.RequestedValue is null)
        {
            return descriptor.SupportsAction
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
        }

        var value = command.RequestedValue;
        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match descriptor kind {descriptor.ValueKind}.");
        }

        if (ValidateCommandValue(value, descriptor) is { } invalid)
        {
            return invalid;
        }

        // WSGM decides both limits of the pair; a command that does not carry a valid one is refused.
        return DevicePowerPair.Peer(_descriptorSet.Descriptors, command.CapabilityId) is not null
               && !DevicePowerPair.TryResolve(command, _descriptorSet.Descriptors, out _, out _, out var pairError)
            ? ValueOutOfRange(pairError!)
            : null;
    }

    private static CapabilityReason? ValidateCommandValue(
        CapabilityValue value,
        CapabilityDescriptor descriptor)
    {
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer:
                if (value.IntegerValue is not { } integer)
                {
                    return ValueOutOfRange("No integer value was supplied.");
                }

                if (descriptor.Minimum is { } minimum && integer < minimum)
                {
                    return ValueOutOfRange($"{integer} is below the minimum of {minimum}.");
                }

                if (descriptor.Maximum is { } maximum && integer > maximum)
                {
                    return ValueOutOfRange($"{integer} is above the maximum of {maximum}.");
                }

                if (descriptor.Step is not ({ } step and > 0))
                {
                    return null;
                }

                var origin = descriptor.Minimum ?? 0;
                if ((integer - origin) % step != 0)
                {
                    return ValueOutOfRange(
                        $"{integer} is not on the {step} step boundary from {origin}.");
                }

                return null;

            case CapabilityValueKind.Choice:
                if (value.ChoiceValue is not { Length: > 0 } choice)
                {
                    return ValueOutOfRange("No choice was supplied.");
                }

                return descriptor.Choices.Any(candidate => string.Equals(
                    candidate.Value,
                    choice,
                    StringComparison.Ordinal))
                    ? null
                    : ValueOutOfRange($"'{choice}' is not one of the declared options.");

            case CapabilityValueKind.Boolean:
                return value.BooleanValue is not null
                    ? null
                    : ValueOutOfRange("No boolean value was supplied.");

            case CapabilityValueKind.Color:
                if (value.ColorValue is not { } color)
                {
                    return ValueOutOfRange("No colour was supplied.");
                }

                return color is >= 0 and <= 0xFFFFFF
                    ? null
                    : ValueOutOfRange("Colour must be 24-bit RGB.");

            case CapabilityValueKind.Curve:
                if (value.CurveValue.Count == 0)
                {
                    return ValueOutOfRange("Curve has no points.");
                }

                for (var index = 1; index < value.CurveValue.Count; index++)
                {
                    if (value.CurveValue[index].Input <= value.CurveValue[index - 1].Input)
                    {
                        return ValueOutOfRange(
                            "Curve points must be strictly increasing in input.");
                    }
                }

                return null;

            case CapabilityValueKind.None:
            case CapabilityValueKind.Text:
            default:
                return new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    $"Value kind {descriptor.ValueKind} carries no value.");
        }
    }

    private static CapabilityReason ValueOutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }

    private static bool FirmwareVerified(ClawIdentityState identity, FirmwareKind kind)
    {
        return kind is not FirmwareKind.Wmi || identity.WmiAvailable;
    }

    private static CapabilityCommandResult NormalizeCommandResult(
        CapabilityCommand command,
        CapabilityCommandResult result)
    {
        if (result.CommandId != command.CommandId)
        {
            return Indeterminate(command, "Capability handler returned a result for another command.");
        }

        if (result.Outcome is CommandOutcome.AppliedVerified && result.ReadbackValue is null)
        {
            return result with
            {
                Outcome = CommandOutcome.AppliedUnverified,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Handler claimed verified application without readback evidence.")
            };
        }

        return result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null
            ? result with { ReadbackValue = null }
            : result;
    }

    private async ValueTask<CapabilityCommandResult> JournalCommandAsync(
        string serviceId,
        string firmwareIdentity,
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<ClawRecoveryState>> readOriginal,
        ClawCommandHandler apply,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("The recovery journal is unavailable.");
        }

        ClawWriteBudget.Require(command.Deadline, "journalled command preparation");
        ClawRecoveryState originalState;
        try
        {
            originalState = await readOriginal(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // HC writes without capturing anything. When the original cannot be read, the command is
            // written the same way and there is nothing to restore; a read never gates a write.
            PluginTrace.Failure(serviceId, "The original state could not be captured; writing without a restore", ex);
            return await apply(command, cancellationToken).ConfigureAwait(false);
        }

        var operation = await _journal.BeginAsync(
            serviceId,
            firmwareIdentity,
            originalState,
            cancellationToken).ConfigureAwait(false);
        ClawWriteBudget.Require(command.Deadline, "journalled hardware application");
        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = await apply(command, cancellationToken).ConfigureAwait(false);

        await _journal.CompleteCommandAsync(operation, result, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Rollback is not RollbackResult.RestoreFailed)
        {
            return result;
        }

        DeviceServiceStatus? service = serviceId switch
        {
            ServiceIds.Power => _power,
            ServiceIds.Fans => _fans,
            _ => null
        };
        if (service is null)
        {
            return result;
        }

        CapabilityReason reason = new(
            CapabilityReasonCode.TransportFaulted,
            "A command rollback failed; the resource is faulted until reconciliation.");
        service.ReconciliationBlockReason = reason;
        service.Fault(reason);

        return result;
    }

    // Admission succeeded and the handler failed without confirming a rollback. Journalled resources
    // remain outstanding, so claiming any restoration here would be fabricated.
    private static CapabilityCommandResult Indeterminate(CapabilityCommand command, string detail)
    {
        return CommandResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            detail,
            RollbackResult.RestoreFailed);
    }

    private static ValueTask<CapabilityCommandResult> ReadOnlyHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommandResults.Rejected(
            command,
            CapabilityReasonCode.Unsupported,
            "This capability is read-only."));
    }

    private delegate ValueTask<CapabilityCommandResult> ClawCommandHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    private enum FirmwareKind
    {
        None,
        Wmi
    }
}
