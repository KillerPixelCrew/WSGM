using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// The immutable description of one capability instance: what it is, what values it accepts, and
/// what applying it costs.
/// </summary>
/// <remarks>
/// A descriptor never changes. When firmware, the endpoint set, or dependency health changes what a
/// capability can do, the plugin publishes a complete replacement set under a new
/// <see cref="CapabilityDescriptorSet.Generation"/> and consumers discard everything they cached.
/// Mutating a descriptor in place would let a stale range validate a command the hardware will
/// reject.
/// <para>
/// A descriptor is a description, not a promise. WSGM validates a request against it for UI
/// consistency; the plugin revalidates authoritatively against current firmware and state on every
/// command, because a value that was legal when the descriptor was published may not be legal now.
/// </para>
/// </remarks>
public sealed record CapabilityDescriptor
{
    /// <summary>Stable capability identifier, for example <c>power.primary-limit</c>.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    /// Instance discriminator when a device has several of the same capability, such as two fans.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>What this capability means to WSGM.</summary>
    public required CapabilityRole Role { get; init; }

    /// <summary>The shape of its value.</summary>
    public required CapabilityValueKind ValueKind { get; init; }

    /// <summary>How WSGM labels it.</summary>
    public required CapabilityDisplay Display { get; init; }

    /// <summary>Whether the current value can be read back from hardware.</summary>
    public bool SupportsRead { get; init; }

    /// <summary>Whether a new value can be written.</summary>
    public bool SupportsWrite { get; init; }

    /// <summary>Whether the capability can be invoked as a one-shot action.</summary>
    public bool SupportsAction { get; init; }

    /// <summary>Inclusive minimum for an integer capability.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive maximum for an integer capability.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values.</summary>
    public int? Step { get; init; }

    /// <summary>Unit of a numeric value.</summary>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>Legal options for a choice capability.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>
    /// Longest accepted value for a <see cref="CapabilityValueKind.Text"/> capability.
    /// </summary>
    /// <remarks>
    /// Required for text, ignored otherwise. There is no default: a text capability that declared no
    /// bound would be the one value shape with no limit at all, which is exactly what
    /// <see cref="PlainText"/> exists to prevent.
    /// </remarks>
    public int? MaximumLength { get; init; }

    /// <summary>Whether the capability is available while running on AC power.</summary>
    public bool AvailableOnAc { get; init; } = true;

    /// <summary>
    /// Whether the capability is available while running on battery.
    /// </summary>
    /// <remarks>
    /// AC/DC is a descriptor field, not a descriptor generation: the power source changes constantly
    /// and republishing every descriptor on each transition would invalidate caches for no reason.
    /// The live condition is reported through capability state instead.
    /// </remarks>
    public bool AvailableOnDc { get; init; } = true;

    /// <summary>How long a written value survives.</summary>
    public required CapabilityPersistence Persistence { get; init; }
}

/// <summary>One legal option of a choice capability.</summary>
/// <param name="Value">Stable machine value, used in commands and persisted state.</param>
/// <param name="Display">How WSGM labels the option.</param>
public sealed record CapabilityChoice(string Value, CapabilityDisplay Display);

/// <summary>How long a written capability value survives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityPersistence>))]
public enum CapabilityPersistence
{
    /// <summary>Not established. Treated as device-persistent by every safety rule.</summary>
    Unknown,

    /// <summary>Lost when the device loses power.</summary>
    Volatile,

    /// <summary>Stored on the device and survives reboot.</summary>
    DevicePersistent,
}

/// <summary>
/// A complete, versioned set of descriptors for one device generation.
/// </summary>
/// <remarks>
/// Descriptors are always published as a whole set. A capability missing from a new set has gone
/// away and its control disappears, rather than lingering as permanently unavailable.
/// </remarks>
public sealed record CapabilityDescriptorSet
{
    /// <summary>
    /// Monotonic generation. Increments whenever any descriptor changes.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>The device generation these descriptors describe.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Every capability the device currently offers.</summary>
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; init; } = [];
}
