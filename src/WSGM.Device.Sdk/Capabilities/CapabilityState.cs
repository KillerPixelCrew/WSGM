using System;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// How much a reported hardware value can be trusted.
/// </summary>
/// <remarks>
/// The distinction that matters most is <see cref="Observed"/> versus <see cref="Verified"/>: a
/// successful IPC reply is not a hardware readback. A plugin that accepted a command and got no
/// error has <see cref="Observed"/> at best; only an independent read of the value it wrote earns
/// <see cref="Verified"/>. Collapsing the two would let the UI show a value the hardware never took.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<HardwareStateQuality>))]
public enum HardwareStateQuality
{
    /// <summary>Never read. No claim is made about the hardware.</summary>
    Unknown,

    /// <summary>Read from hardware, without independent confirmation.</summary>
    Observed,

    /// <summary>Read back and confirmed to match what was applied.</summary>
    Verified,

    /// <summary>Was valid, but the observation has expired or its generation is gone.</summary>
    Stale,

    /// <summary>The capability failed and its value cannot be trusted at all.</summary>
    Faulted,
}

/// <summary>
/// The live state of one capability as reported by the plugin.
/// </summary>
/// <remarks>
/// Versioned separately from the descriptor because it changes constantly while the descriptor does
/// not. It carries only what the plugin observed; WSGM's desired value and UI progress live in
/// WSGM's capability projection and are never mixed in here — a plugin does not own what the
/// user asked for.
/// </remarks>
public sealed record CapabilityState
{
    /// <summary>Capability this state belongs to.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Instance discriminator, matching the descriptor.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Whether the capability can currently be used.</summary>
    public required bool Available { get; init; }

    /// <summary>Why it is unavailable or degraded. Null when it is healthy.</summary>
    public CapabilityReason? Reason { get; init; }

    /// <summary>The value observed on the hardware, in the descriptor's value shape.</summary>
    public CapabilityValue? ObservedValue { get; init; }

    /// <summary>How much <see cref="ObservedValue"/> can be trusted.</summary>
    public required HardwareStateQuality Quality { get; init; }

    /// <summary>When the observation was taken, in UTC.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Descriptor generation this state was produced against.</summary>
    public required long DescriptorGeneration { get; init; }

    /// <summary>Process/reconnect cycle generation this state was produced against.</summary>
    public required long CycleGeneration { get; init; }
}

/// <summary>
/// A capability value in whichever shape its descriptor declares.
/// </summary>
/// <remarks>
/// A closed set of shapes rather than an opaque payload: an arbitrary blob would be a passthrough,
/// and a passthrough is how device-specific structure leaks into a semantic contract.
/// </remarks>
public sealed record CapabilityValue
{
    /// <summary>Which field carries the value.</summary>
    public required CapabilityValueKind Kind { get; init; }

    /// <summary>Value when <see cref="Kind"/> is <see cref="CapabilityValueKind.Boolean"/>.</summary>
    public bool? BooleanValue { get; init; }

    /// <summary>Value when <see cref="Kind"/> is <see cref="CapabilityValueKind.Integer"/>.</summary>
    public int? IntegerValue { get; init; }

    /// <summary>Selected option when <see cref="Kind"/> is <see cref="CapabilityValueKind.Choice"/>.</summary>
    public string? ChoiceValue { get; init; }

    /// <summary>Packed 24-bit RGB when <see cref="Kind"/> is <see cref="CapabilityValueKind.Color"/>.</summary>
    public int? ColorValue { get; init; }

    /// <summary>Curve points when <see cref="Kind"/> is <see cref="CapabilityValueKind.Curve"/>.</summary>
    public System.Collections.Generic.IReadOnlyList<CurvePoint> CurveValue { get; init; } = [];
}

/// <summary>One point of a capability curve, such as a fan table entry.</summary>
/// <param name="Input">The independent value, for example a temperature in Celsius.</param>
/// <param name="Output">The dependent value, for example a duty cycle in percent.</param>
public readonly record struct CurvePoint(int Input, int Output);
