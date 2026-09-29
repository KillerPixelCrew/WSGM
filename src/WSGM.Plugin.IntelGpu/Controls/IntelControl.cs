using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>What one read returned.</summary>
/// <param name="Value">The value in the descriptor's shape, or null when it could not be read.</param>
/// <param name="Result">The driver result behind it, for diagnostics.</param>
/// <param name="Available">Whether the control can be used right now.</param>
/// <param name="Reason">Why it cannot, when it cannot.</param>
internal readonly record struct ControlRead(
    CapabilityValue? Value,
    int Result,
    bool Available = true,
    CapabilityReason? Reason = null)
{
    public static ControlRead Of(CapabilityValue value)
    {
        return new ControlRead(value, IgclResult.Success);
    }

    public static ControlRead Failed(int result)
    {
        return new ControlRead(null, result);
    }
}

/// <summary>How a write ended at the driver.</summary>
internal enum WriteStatus
{
    /// <summary>The driver accepted it.</summary>
    Applied,

    /// <summary>The driver validated and refused it; nothing was changed.</summary>
    Refused,

    /// <summary>The driver failed mid-call; whether anything changed is unknown.</summary>
    Uncertain
}

/// <summary>What one write returned.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Result">The driver result.</param>
/// <param name="Detail">Plain diagnostic text.</param>
internal readonly record struct ControlWrite(WriteStatus Status, int Result, string? Detail = null)
{
    public static ControlWrite Applied { get; } = new(WriteStatus.Applied, IgclResult.Success);

    /// <summary>Classifies a set call's result.</summary>
    /// <param name="result">The driver result.</param>
    /// <param name="what">What was being written, for the detail.</param>
    /// <returns>Applied, refused or uncertain.</returns>
    public static ControlWrite From(int result, string what)
    {
        if (result == IgclResult.Success)
        {
            return Applied;
        }

        return new ControlWrite(
            IgclResult.IsRefusal(result) ? WriteStatus.Refused : WriteStatus.Uncertain,
            result,
            $"The driver answered {IgclResult.Describe(result)} to {what}.");
    }

    public static ControlWrite Refuse(string detail)
    {
        return new ControlWrite(WriteStatus.Refused, IgclResult.InvalidArgument, detail);
    }
}

/// <summary>
///     One published capability instance and the driver path behind it.
/// </summary>
/// <remarks>
///     A control is built from what the driver reported at the start of a session and lives until the
///     session closes. Reads and writes run on the plugin's lane; nothing here is thread-safe on its own.
///     A write never reads first to decide whether to write, and a read never gates a write: the plugin
///     writes, reads back to grade the outcome, and publishes the written value.
/// </remarks>
internal abstract class IntelControl
{
    protected IntelControl(CapabilityDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>The descriptor as published.</summary>
    public CapabilityDescriptor Descriptor { get; }

    /// <summary>The capability id.</summary>
    public string CapabilityId => Descriptor.CapabilityId;

    /// <summary>The instance id.</summary>
    public string? InstanceId => Descriptor.InstanceId;

    /// <summary>Reads the current value.</summary>
    /// <returns>The value, or a failed read.</returns>
    public abstract ControlRead Read();

    /// <summary>Writes a value the plugin already validated against the descriptor.</summary>
    /// <param name="value">The value.</param>
    /// <returns>How the driver answered.</returns>
    public abstract ControlWrite Write(CapabilityValue value);

    /// <summary>Whether a readback confirms the value written.</summary>
    /// <param name="written">What was written.</param>
    /// <param name="readback">What was read afterwards.</param>
    /// <returns><see langword="true" /> when they agree.</returns>
    public virtual bool Confirms(CapabilityValue written, CapabilityValue? readback)
    {
        return readback is not null && ValueEquals(written, readback);
    }

    /// <summary>Validates a requested value against the descriptor.</summary>
    /// <param name="value">The request.</param>
    /// <param name="error">Why it is not acceptable.</param>
    /// <returns><see langword="true" /> when it may be written.</returns>
    public virtual bool Validate(CapabilityValue? value, out string? error)
    {
        if (value is null || value.Kind != Descriptor.ValueKind)
        {
            error = $"A {Descriptor.ValueKind} value is required.";
            return false;
        }

        switch (value.Kind)
        {
            case CapabilityValueKind.Boolean when value.BooleanValue is null:
                error = "The boolean is missing.";
                return false;
            case CapabilityValueKind.Integer when value.IntegerValue is not { } integer
                                                  || integer < Descriptor.Minimum
                                                  || integer > Descriptor.Maximum:
                error = $"The value must be {Descriptor.Minimum}-{Descriptor.Maximum}.";
                return false;
            case CapabilityValueKind.Choice when Descriptor.Choices.All(choice => choice.Value != value.ChoiceValue):
                error = $"'{value.ChoiceValue}' is not an offered choice.";
                return false;
            default:
                error = null;
                return true;
        }
    }

    /// <summary>Whether two values of one shape are equal.</summary>
    /// <param name="left">One value.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true" /> when they carry the same value.</returns>
    public static bool ValueEquals(CapabilityValue? left, CapabilityValue? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Kind == right.Kind
               && left.BooleanValue == right.BooleanValue
               && left.IntegerValue == right.IntegerValue
               && left.ChoiceValue == right.ChoiceValue;
    }
}
