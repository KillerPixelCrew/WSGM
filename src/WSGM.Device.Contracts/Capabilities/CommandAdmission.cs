using System;
using System.Linq;

namespace WSGM.Device.Contracts.Capabilities;

/// <summary>
/// Whether a command may be applied at all, checked before anything reaches hardware.
/// </summary>
/// <remarks>
/// Both sides run this, and that duplication is deliberate. WSGM checks it to keep the UI honest and
/// to avoid sending work that will obviously fail. The plugin checks it again — against current
/// firmware and current hardware state, which only it can see — because a value that was legal when
/// the descriptor was published may not be legal now. Neither side may skip its check on the grounds
/// that the other one ran.
/// </remarks>
public static class CommandAdmission
{
    /// <summary>Why a command was refused before reaching hardware.</summary>
    /// <param name="Admitted">Whether the command may proceed.</param>
    /// <param name="Reason">The refusal, or null when admitted.</param>
    public sealed record Result(bool Admitted, CapabilityReason? Reason)
    {
        /// <summary>A command that may proceed.</summary>
        public static Result Admit { get; } = new(true, null);

        /// <summary>Refuses a command with a structured reason.</summary>
        /// <param name="code">Why it was refused.</param>
        /// <param name="detail">Diagnostic detail.</param>
        /// <param name="retryable">Whether the same request could succeed later.</param>
        /// <returns>A refusing result.</returns>
        public static Result Refuse(CapabilityReasonCode code, string detail, bool retryable = false) =>
            new(false, new CapabilityReason(code, detail, retryable));
    }

    /// <summary>
    /// Decides whether a command may be applied.
    /// </summary>
    /// <param name="command">The requested command.</param>
    /// <param name="descriptor">The descriptor the command targets.</param>
    /// <param name="currentDescriptorGeneration">The descriptor generation now in effect.</param>
    /// <param name="currentDeviceGeneration">The device generation now in effect.</param>
    /// <param name="onAcPower">Whether the machine is currently on AC power.</param>
    /// <param name="now">Current time, in UTC.</param>
    /// <returns>Whether it may proceed, and why not when it may not.</returns>
    public static Result Evaluate(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        long currentDescriptorGeneration,
        long currentDeviceGeneration,
        bool onAcPower,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(descriptor);

        // Generations first. A command authored against a superseded descriptor was validated
        // against a range that no longer exists, so nothing else about it is worth checking.
        if (command.ExpectedDescriptorGeneration != currentDescriptorGeneration)
        {
            return Result.Refuse(CapabilityReasonCode.GenerationChanged,
                $"Command targets descriptor generation {command.ExpectedDescriptorGeneration}; "
                    + $"current is {currentDescriptorGeneration}.",
                retryable: true);
        }

        if (command.ExpectedDeviceGeneration != currentDeviceGeneration)
        {
            return Result.Refuse(CapabilityReasonCode.GenerationChanged,
                $"Command targets device generation {command.ExpectedDeviceGeneration}; "
                    + $"current is {currentDeviceGeneration}.",
                retryable: true);
        }

        if (command.Deadline <= now)
        {
            return Result.Refuse(CapabilityReasonCode.Quiescing,
                "Command deadline passed before it could be applied.", retryable: true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return Result.Refuse(CapabilityReasonCode.UnavailableOnPowerSource,
                onAcPower
                    ? "Capability is not available on AC power."
                    : "Capability is not available on battery.");
        }

        if (command.RequestedValue is null)
        {
            return descriptor.SupportsAction
                ? Result.Admit
                : Result.Refuse(CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return Result.Refuse(CapabilityReasonCode.Unsupported, "Capability is read-only.");
        }

        return ValidateValue(command.RequestedValue, descriptor);
    }

    private static Result ValidateValue(CapabilityValue value, CapabilityDescriptor descriptor)
    {
        if (value.Kind != descriptor.ValueKind)
        {
            return Result.Refuse(CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match descriptor kind {descriptor.ValueKind}.");
        }

        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer:
                if (value.IntegerValue is not { } integer)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange, "No integer value supplied.");
                }

                if (descriptor.Minimum is { } min && integer < min)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                        $"{integer} is below the minimum of {min}.");
                }

                if (descriptor.Maximum is { } max && integer > max)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                        $"{integer} is above the maximum of {max}.");
                }

                // Step is measured from the minimum, not from zero: a range of 8-30 W in steps of 3
                // means 8, 11, 14 - not 9, 12, 15.
                if (descriptor.Step is { } step and > 0)
                {
                    int origin = descriptor.Minimum ?? 0;
                    if ((integer - origin) % step != 0)
                    {
                        return Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                            $"{integer} is not on the {step} step boundary from {origin}.");
                    }
                }

                return Result.Admit;

            case CapabilityValueKind.Choice:
                if (value.ChoiceValue is not { Length: > 0 } choice)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange, "No choice supplied.");
                }

                return descriptor.Choices.Any(c =>
                        string.Equals(c.Value, choice, StringComparison.Ordinal))
                    ? Result.Admit
                    : Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                        $"'{choice}' is not one of the declared options.");

            case CapabilityValueKind.Boolean:
                return value.BooleanValue is not null
                    ? Result.Admit
                    : Result.Refuse(CapabilityReasonCode.ValueOutOfRange, "No boolean value supplied.");

            case CapabilityValueKind.Color:
                if (value.ColorValue is not { } color)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange, "No colour supplied.");
                }

                return color is >= 0 and <= 0xFFFFFF
                    ? Result.Admit
                    : Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                        "Colour must be 24-bit RGB.");

            case CapabilityValueKind.Curve:
                if (value.CurveValue.Count == 0)
                {
                    return Result.Refuse(CapabilityReasonCode.ValueOutOfRange, "Curve has no points.");
                }

                // A non-monotonic fan curve is not a preference, it is a table the firmware will
                // interpret unpredictably.
                for (int i = 1; i < value.CurveValue.Count; i++)
                {
                    if (value.CurveValue[i].Input <= value.CurveValue[i - 1].Input)
                    {
                        return Result.Refuse(CapabilityReasonCode.ValueOutOfRange,
                            "Curve points must be strictly increasing in input.");
                    }
                }

                return Result.Admit;

            default:
                return Result.Refuse(CapabilityReasonCode.Unsupported,
                    $"Value kind {descriptor.ValueKind} carries no value.");
        }
    }
}
