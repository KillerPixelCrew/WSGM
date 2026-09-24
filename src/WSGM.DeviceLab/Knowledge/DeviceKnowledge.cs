using System.Collections.Generic;

using WSGM.Device.Sdk.Identity;

namespace WSGM.DeviceLab.Knowledge;

/// <summary>How much of a knowledge record a person has reviewed against hardware or a plugin.</summary>
internal enum DeviceKnowledgeStatus
{
    /// <summary>Generated from Handheld Companion source; nothing in it has been checked on hardware.</summary>
    Extracted,

    /// <summary>Hand-written from WSGM plugins, lab runs and reviewed HC source.</summary>
    Curated
}

/// <summary>Where one fact in a knowledge record came from.</summary>
internal enum DeviceKnowledgeSource
{
    /// <summary>Read out of the decompiled Handheld Companion source.</summary>
    HcDerived,

    /// <summary>Taken from a WSGM device plugin, which was checked on hardware.</summary>
    WsgmPlugin,

    /// <summary>Observed in an attended lab run.</summary>
    LabObserved,

    /// <summary>Confirmed by a tester in the Device Lab wizard.</summary>
    LabConfirmed
}

/// <summary>A pointer to the evidence behind one fact.</summary>
internal sealed record DeviceKnowledgeProvenance
{
    /// <summary>The kind of evidence.</summary>
    public required DeviceKnowledgeSource Source { get; init; }

    /// <summary>File and line, plugin path, or lab run folder.</summary>
    public required string Reference { get; init; }

    /// <summary>What the evidence says or how far it can be trusted.</summary>
    public string? Note { get; init; }
}

/// <summary>
///     One known handheld: how to recognise it and what its hardware is believed to do.
/// </summary>
/// <remarks>
///     A record is evidence for the wizard to test, not a trusted driver. Extracted records carry
///     only what could be read from HC's constructors and device switch; the wizard runs read-only
///     stages for them. Write mechanisms are exercised only from curated records.
/// </remarks>
internal sealed record DeviceKnowledgeRecord
{
    /// <summary>Record schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable record ID, for example <c>hc.rog-ally</c> or <c>wsgm.claw-8-a2vm</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Product name shown to the tester.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Review state of the record as a whole.</summary>
    public required DeviceKnowledgeStatus Status { get; init; }

    /// <summary>The HC device class the record describes, when there is one.</summary>
    public string? HcClass { get; init; }

    /// <summary>The HC classes this one derives from, nearest first.</summary>
    public IReadOnlyList<string> HcBaseClasses { get; init; } = [];

    /// <summary>Extracted record IDs this curated record replaces.</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];

    /// <summary>Alternative identity rules; any one matching identifies the device.</summary>
    public IReadOnlyList<HardwareMatchRule> Identity { get; init; } = [];

    /// <summary>Power ranges the vendor or HC declares.</summary>
    public DevicePowerKnowledge? Power { get; init; }

    /// <summary>HC capability flag names, for example <c>FanControl</c>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>HC lighting mode flag names.</summary>
    public IReadOnlyList<string> LightingModes { get; init; } = [];

    /// <summary>HID endpoints the device's own controller or vendor interface exposes.</summary>
    public IReadOnlyList<DeviceHidEndpointKnowledge> HidEndpoints { get; init; } = [];

    /// <summary>Buttons and how each is believed to arrive.</summary>
    public IReadOnlyList<DeviceButtonKnowledge> Buttons { get; init; } = [];

    /// <summary>Motion sensor sources and axis maps.</summary>
    public DeviceMotionKnowledge? Motion { get; init; }

    /// <summary>Fan, lighting, power, charge-limit and init mechanisms.</summary>
    public IReadOnlyList<DeviceMechanismKnowledge> Mechanisms { get; init; } = [];

    /// <summary>HC members the class overrides, which is where unextracted behaviour lives.</summary>
    public IReadOnlyList<string> HcOverrides { get; init; } = [];

    /// <summary>Known mistakes in the source data and behaviour the wizard must not copy.</summary>
    public IReadOnlyList<string> Hazards { get; init; } = [];

    /// <summary>Evidence for the record as a whole.</summary>
    public IReadOnlyList<DeviceKnowledgeProvenance> Provenance { get; init; } = [];
}

/// <summary>Power ranges in watts and clocks in MHz.</summary>
internal sealed record DevicePowerKnowledge
{
    /// <summary>HC's nominal TDP triple (STAPM, slow, fast).</summary>
    public IReadOnlyList<double> NominalWatts { get; init; } = [];

    /// <summary>HC's configurable TDP range (minimum, maximum).</summary>
    public IReadOnlyList<double> ConfigurableWatts { get; init; } = [];

    /// <summary>GPU clock range (minimum, maximum).</summary>
    public IReadOnlyList<double> GpuClockMhz { get; init; } = [];

    /// <summary>Maximum CPU clock.</summary>
    public int? CpuClockMhz { get; init; }
}

/// <summary>A HID interface identified by USB IDs and, optionally, a top-level collection.</summary>
internal sealed record DeviceHidEndpointKnowledge
{
    /// <summary>What the endpoint is for, for example <c>controller</c> or <c>vendor</c>.</summary>
    public required string Role { get; init; }

    /// <summary>USB vendor ID as four hex digits.</summary>
    public required string VendorId { get; init; }

    /// <summary>USB product IDs as four hex digits.</summary>
    public IReadOnlyList<string> ProductIds { get; init; } = [];

    /// <summary>Top-level collection usage page, when the endpoint is one collection.</summary>
    public int? UsagePage { get; init; }

    /// <summary>Top-level collection usage.</summary>
    public int? Usage { get; init; }

    /// <summary>Where this came from.</summary>
    public DeviceKnowledgeProvenance? Provenance { get; init; }
}

/// <summary>How a button press is believed to reach Windows.</summary>
internal enum DeviceButtonSourceKind
{
    /// <summary>The firmware types a key or key combination.</summary>
    KeyboardChord,

    /// <summary>A bit in a vendor HID input report.</summary>
    HidReport,

    /// <summary>A WMI event carries a code.</summary>
    WmiEvent,

    /// <summary>The standard gamepad report or XInput state.</summary>
    Gamepad,

    /// <summary>
    ///     HC declares the button but reads it somewhere its constructor does not show; the
    ///     wizard has to find the source.
    /// </summary>
    Declared
}

/// <summary>One button and the source it is believed to arrive on.</summary>
internal sealed record DeviceButtonKnowledge
{
    /// <summary>Name as the vendor or HC calls it, for example <c>CC</c> or <c>M1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The wizard button this is, once someone has mapped it.</summary>
    public string? WizardButton { get; init; }

    /// <summary>HC's ButtonFlags value, for extracted records.</summary>
    public string? HcFlag { get; init; }

    /// <summary>The believed source.</summary>
    public required DeviceButtonSourceKind Source { get; init; }

    /// <summary>Keys the firmware sends on press, as HC KeyCode names.</summary>
    public IReadOnlyList<string> PressKeys { get; init; } = [];

    /// <summary>Keys the firmware sends on release.</summary>
    public IReadOnlyList<string> ReleaseKeys { get; init; } = [];

    /// <summary>Whether HC swallows the keys without mapping them to a button.</summary>
    public bool Silenced { get; init; }

    /// <summary>HID report ID for a report source.</summary>
    public int? ReportId { get; init; }

    /// <summary>Byte offset in the report, counted with the report ID at 0.</summary>
    public int? ByteOffset { get; init; }

    /// <summary>Bit mask, or the exact byte value when <see cref="MatchesValue" /> is set.</summary>
    public int? Mask { get; init; }

    /// <summary>Whether <see cref="Mask" /> is a whole-byte value rather than a bit mask.</summary>
    public bool MatchesValue { get; init; }

    /// <summary>WMI event code for a WMI source.</summary>
    public int? EventCode { get; init; }

    /// <summary>Where this came from.</summary>
    public DeviceKnowledgeProvenance? Provenance { get; init; }
}

/// <summary>Motion sensors and the axis maps believed to align them with the device.</summary>
internal sealed record DeviceMotionKnowledge
{
    /// <summary>Gyrometer axis map as HC applies it.</summary>
    public DeviceAxisMap? Gyrometer { get; init; }

    /// <summary>Accelerometer axis map as HC applies it.</summary>
    public DeviceAxisMap? Accelerometer { get; init; }

    /// <summary>Custom legacy Sensor API fields, for sensors that do not use the standard ones.</summary>
    public IReadOnlyList<DeviceLegacySensorFields> LegacyFields { get; init; } = [];

    /// <summary>Where this came from.</summary>
    public DeviceKnowledgeProvenance? Provenance { get; init; }
}

/// <summary>
///     An axis remap and sign flip, in HC's convention.
/// </summary>
/// <remarks>
///     HC writes <c>out[Swap[k]] = raw[k]</c> and then multiplies each output axis by its sign, so the
///     swap key is the raw axis and the value is the output axis.
/// </remarks>
internal sealed record DeviceAxisMap
{
    /// <summary>Raw axis to output axis, keys and values from X, Y and Z.</summary>
    public required IReadOnlyDictionary<string, string> Swap { get; init; }

    /// <summary>Sign applied to each output axis, +1 or -1.</summary>
    public required IReadOnlyDictionary<string, int> Sign { get; init; }
}

/// <summary>The legacy Sensor API fields one sensor reports its axes in.</summary>
internal sealed record DeviceLegacySensorFields
{
    /// <summary>Which sensor, <c>gyrometer</c> or <c>accelerometer</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The sensor's friendly name.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Property key format ID.</summary>
    public required string FormatId { get; init; }

    /// <summary>Property IDs for X, Y and Z.</summary>
    public IReadOnlyList<int> PropertyIds { get; init; } = [];
}

/// <summary>
///     One hardware mechanism, written as data against a generic transport.
/// </summary>
/// <remarks>
///     Parameters are strings so one shape fits every transport; numbers use <c>0x</c> hex. The
///     transport implementation owns parsing and bounds.
/// </remarks>
internal sealed record DeviceMechanismKnowledge
{
    /// <summary>What the mechanism controls, for example <c>fan</c>, <c>lighting</c> or <c>tdp</c>.</summary>
    public required string Feature { get; init; }

    /// <summary>Transport name, for example <c>superio-ec</c>, <c>atkacpi</c> or <c>wmi-method</c>.</summary>
    public required string Transport { get; init; }

    /// <summary>Transport parameters.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether a readback is known to exist for this mechanism.</summary>
    public bool HasReadback { get; init; }

    /// <summary>Anything a person applying the mechanism must know.</summary>
    public string? Note { get; init; }

    /// <summary>Where this came from.</summary>
    public DeviceKnowledgeProvenance? Provenance { get; init; }
}
