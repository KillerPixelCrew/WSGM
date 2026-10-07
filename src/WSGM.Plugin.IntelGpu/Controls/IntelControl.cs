using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>Where a failure came from.</summary>
internal enum FailureKind
{
    /// <summary>The driver answered with an IGCL result other than success.</summary>
    DriverResult,

    /// <summary>A call threw.</summary>
    Exception,

    /// <summary>The registry could not be read or written.</summary>
    Registry,

    /// <summary>The plugin refused the value before it reached the driver.</summary>
    Refused
}

/// <summary>Why a read or write did not succeed, in terms of whatever transport carried it.</summary>
/// <param name="Kind">Where it came from.</param>
/// <param name="Result">The IGCL result, for <see cref="FailureKind.DriverResult" />.</param>
/// <param name="Detail">Plain diagnostic text for every other kind.</param>
internal readonly record struct ControlFailure(FailureKind Kind, int Result = 0, string? Detail = null)
{
    public static ControlFailure Driver(int result)
    {
        return new ControlFailure(FailureKind.DriverResult, result);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind == FailureKind.DriverResult
            ? $"the driver answered {IgclResult.Describe(Result)}"
            : Detail ?? Kind.ToString();
    }
}

/// <summary>What one read returned.</summary>
/// <param name="Value">The value in the descriptor's shape, or null when it is unknown or unreadable.</param>
/// <param name="Available">Whether the control can be used right now.</param>
/// <param name="Reason">Why it cannot, when it cannot.</param>
/// <param name="Failure">Why it could not be read, or null when the read succeeded.</param>
internal readonly record struct ControlRead(
    CapabilityValue? Value,
    bool Available = true,
    CapabilityReason? Reason = null,
    ControlFailure? Failure = null)
{
    /// <summary>A successful read; a null value is one the descriptor has no equivalent for.</summary>
    /// <param name="value">The value, or null when unknown.</param>
    /// <returns>The read.</returns>
    public static ControlRead Of(CapabilityValue? value)
    {
        return new ControlRead(value);
    }

    /// <summary>A read the driver answered with anything but success.</summary>
    /// <param name="result">The IGCL result.</param>
    /// <returns>The read.</returns>
    public static ControlRead Driver(int result)
    {
        return new ControlRead(null, Failure: ControlFailure.Driver(result));
    }

    /// <summary>A read that failed another way.</summary>
    /// <param name="failure">Why.</param>
    /// <returns>The read.</returns>
    public static ControlRead Failed(ControlFailure failure)
    {
        return new ControlRead(null, Failure: failure);
    }

    /// <summary>A control that cannot be used while something else is set, with what the driver holds.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="value">What the driver applies now, when it is worth showing.</param>
    /// <returns>The read.</returns>
    public static ControlRead Unavailable(CapabilityReason reason, CapabilityValue? value = null)
    {
        return new ControlRead(value, false, reason);
    }
}

/// <summary>How a write ended at the driver.</summary>
internal enum WriteStatus
{
    /// <summary>The driver accepted it.</summary>
    Applied,

    /// <summary>The driver or the plugin validated and refused it; nothing was changed.</summary>
    Refused,

    /// <summary>The write failed mid-call; whether anything changed is unknown.</summary>
    Uncertain
}

/// <summary>What one write returned.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Failure">Why it did not apply, or null when it did.</param>
/// <param name="Detail">Plain diagnostic text.</param>
internal readonly record struct ControlWrite(WriteStatus Status, ControlFailure? Failure = null, string? Detail = null)
{
    public static ControlWrite Applied { get; } = new(WriteStatus.Applied);

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
            ControlFailure.Driver(result),
            $"The driver answered {IgclResult.Describe(result)} to {what}.");
    }

    /// <summary>A write refused before anything changed.</summary>
    /// <param name="detail">Why.</param>
    /// <param name="kind">Where the refusal came from.</param>
    /// <returns>The write.</returns>
    public static ControlWrite Refuse(string detail, FailureKind kind = FailureKind.Refused)
    {
        return new ControlWrite(WriteStatus.Refused, new ControlFailure(kind, Detail: detail), detail);
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
///     <para>
///         The rules every control shares live here: a read-only descriptor refuses writes, a value is
///         validated against the descriptor (range and step included) before it reaches the driver, and a
///         choice must name one of the offered members.
///     </para>
/// </remarks>
internal abstract class IntelControl
{
    private static readonly CapabilityValue True = CapabilityValue.Boolean(true);
    private static readonly CapabilityValue False = CapabilityValue.Boolean(false);
    private bool _traced;
    private bool _tracedAvailable;
    private ControlFailure? _tracedFailure;
    private CapabilityValue? _tracedValue;

    protected IntelControl(
        CapabilityDescriptor descriptor,
        IReadOnlyList<EnumMember>? members = null,
        IntegerRange? range = null)
    {
        Descriptor = descriptor;
        Members = members ?? [];
        Range = range
                ?? (descriptor.ValueKind == CapabilityValueKind.Integer
                    ? IntegerRange.Linear(descriptor.Minimum ?? 0, descriptor.Maximum ?? 0, descriptor.Step ?? 1)
                    : null);
        Key = IntelModel.Key(descriptor.CapabilityId, descriptor.InstanceId);
        Name = $"{descriptor.CapabilityId}[{descriptor.InstanceId}]";
    }

    /// <summary>The descriptor as published.</summary>
    public CapabilityDescriptor Descriptor { get; }

    /// <summary>The capability id.</summary>
    public string CapabilityId => Descriptor.CapabilityId;

    /// <summary>The instance id.</summary>
    public string? InstanceId => Descriptor.InstanceId;

    /// <summary>The routing key, built once.</summary>
    public string Key { get; }

    /// <summary>The name trace lines use, built once.</summary>
    public string Name { get; }

    /// <summary>
    ///     The driver structure a support probe exercises. Controls sharing a structure share the key, so the
    ///     plugin probes it once and keeps that one outcome.
    /// </summary>
    public virtual string SupportKey => Key;

    /// <summary>The offered members of a choice, in offer order; empty for other kinds.</summary>
    public IReadOnlyList<EnumMember> Members { get; }

    /// <summary>The integer range of a ranged control, or null for other kinds.</summary>
    public IntegerRange? Range { get; }

    /// <summary>Reads the current value.</summary>
    /// <returns>The value, or a failed read.</returns>
    public abstract ControlRead Read();

    /// <summary>Checks setter support by returning the exact native state before publishing this control.</summary>
    /// <param name="admission">Caller cancellation, deadline and live-session admission, checked immediately before any setter.</param>
    /// <returns>The exact-state probe outcome; the base implementation refuses without calling a setter.</returns>
    public virtual ControlWrite ProbeSupport(WriteAdmission admission)
    {
        return ControlWrite.Refuse("No exact-state support probe is implemented for this control.");
    }

    /// <summary>Validates and writes a value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="admission">Rechecked immediately before each native setter.</param>
    /// <returns>How the driver answered, or a refusal when the descriptor does not allow it.</returns>
    public ControlWrite Write(CapabilityValue value, WriteAdmission admission)
    {
        if (!Descriptor.SupportsWrite)
        {
            return ControlWrite.Refuse($"{CapabilityId} is a status the driver reports; it cannot be set.");
        }

        return Validate(value, out var error)
            ? WriteValidated(value, admission)
            : ControlWrite.Refuse(error ?? "The value is not accepted.");
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
                                                  || Range is not { } range
                                                  || !range.Accepts(integer):
                error = $"The value must be {Descriptor.Minimum}-{Descriptor.Maximum} in steps of {Descriptor.Step}.";
                return false;
            case CapabilityValueKind.Choice when EnumMembers.ById(Members, value.ChoiceValue) is null:
                error = $"'{value.ChoiceValue}' is not an offered choice.";
                return false;
            default:
                error = null;
                return true;
        }
    }

    /// <summary>The published choice for a driver value.</summary>
    /// <param name="value">The driver value.</param>
    /// <returns>The member's choice, or null when no offered member has that value.</returns>
    public CapabilityValue? MemberOf(uint value)
    {
        return EnumMembers.ByValue(Members, value)?.Choice;
    }

    /// <summary>The driver value of a validated choice.</summary>
    /// <param name="value">A choice <see cref="Validate" /> accepted.</param>
    /// <returns>The member's driver value.</returns>
    public uint ValueOf(CapabilityValue value)
    {
        return EnumMembers.ById(Members, value.ChoiceValue)!.Value;
    }

    /// <summary>Whether what a read produced differs from what was last traced, remembering it when it does.</summary>
    /// <param name="value">The value published.</param>
    /// <param name="available">Whether the control is available.</param>
    /// <param name="failure">Why the read failed, if it did.</param>
    /// <returns><see langword="true" /> when a trace line is due.</returns>
    /// <remarks>Checked before a line is formatted, so an unchanged pass builds no strings.</remarks>
    public bool TraceDue(CapabilityValue? value, bool available, ControlFailure? failure)
    {
        if (_traced && _tracedValue == value && _tracedAvailable == available && _tracedFailure == failure)
        {
            return false;
        }

        _traced = true;
        _tracedValue = value;
        _tracedAvailable = available;
        _tracedFailure = failure;
        return true;
    }

    /// <summary>Writes a value <see cref="Validate" /> accepted.</summary>
    /// <param name="value">The value.</param>
    /// <param name="admission">Rechecked immediately before the setter.</param>
    /// <returns>How the driver answered.</returns>
    protected abstract ControlWrite WriteValidated(CapabilityValue value, WriteAdmission admission);

    /// <summary>A published boolean, shared so a read allocates nothing.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value.</returns>
    public static CapabilityValue Boolean(bool value)
    {
        return value ? True : False;
    }
}
