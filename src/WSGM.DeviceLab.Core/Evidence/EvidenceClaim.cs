using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.DeviceLab.Core.Evidence;

/// <summary>
/// One recorded claim about how a device's protocol behaves.
/// </summary>
/// <remarks>
/// A claim is the unit that connects a constant in generated code back to why anyone believes it. The
/// fields are not bookkeeping: <see cref="Counterexamples"/> and <see cref="Limitations"/> exist so a
/// claim carries the evidence against itself alongside the evidence for it, which is the difference
/// between a record and an argument.
/// </remarks>
public sealed record EvidenceClaim
{
    /// <summary>Stable identifier, referenced from generated code and the evidence lock.</summary>
    public required string ClaimId { get; init; }

    /// <summary>Exact device, board, revision, and firmware this claim is scoped to.</summary>
    public required ClaimScope Scope { get; init; }

    /// <summary>Transport the claim concerns, for example a vendor WMI provider or a HID endpoint.</summary>
    public required string Transport { get; init; }

    /// <summary>Endpoint within that transport.</summary>
    public string? Endpoint { get; init; }

    /// <summary>How the value is addressed: a method name, a register, a report ID.</summary>
    public required string Selector { get; init; }

    /// <summary>Byte offset within the response or report.</summary>
    public int? Offset { get; init; }

    /// <summary>Bit mask applied at that offset.</summary>
    public uint? Mask { get; init; }

    /// <summary>Width of the field in bits.</summary>
    public int? WidthBits { get; init; }

    /// <summary>Byte order, when the field is multi-byte.</summary>
    public Endianness Endian { get; init; } = Endianness.Unspecified;

    /// <summary>Multiplier converting the raw value to its unit.</summary>
    public double? Scale { get; init; }

    /// <summary>Unit of the converted value.</summary>
    public string? Unit { get; init; }

    /// <summary>Lowest observed or documented legal value.</summary>
    public double? RangeMinimum { get; init; }

    /// <summary>Highest observed or documented legal value.</summary>
    public double? RangeMaximum { get; init; }

    /// <summary>What the field is believed to mean, in plain language.</summary>
    public required string ProposedMeaning { get; init; }

    /// <summary>How well established the claim is.</summary>
    public required ClaimState State { get; init; }

    /// <summary>Capture event IDs supporting the claim.</summary>
    public IReadOnlyList<string> SupportingObservations { get; init; } = [];

    /// <summary>
    /// Observations that contradict the claim.
    /// </summary>
    /// <remarks>
    /// Recorded rather than discarded. A claim with counterexamples that still reads
    /// <see cref="ClaimState.HardwareVerified"/> is exactly the thing a reviewer needs to see, and
    /// dropping them would make the ledger an argument for the claim instead of a record of it.
    /// </remarks>
    public IReadOnlyList<string> Counterexamples { get; init; } = [];

    /// <summary>How many times the supporting observation was reproduced.</summary>
    public int Repetitions { get; init; }

    /// <summary>Whether the original value was restored and that restoration was verified.</summary>
    public RestorationResult Restoration { get; init; } = RestorationResult.NotApplicable;

    /// <summary>Analyzer that produced the claim, and its version.</summary>
    public string? Analyzer { get; init; }

    /// <summary>Where the claim came from and under what licence it may be used.</summary>
    public required ClaimProvenance Provenance { get; init; }

    /// <summary>Known limits on what the claim establishes.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Claim this one replaces, when it supersedes an earlier finding.</summary>
    public string? Supersedes { get; init; }
}

/// <summary>The exact hardware a claim applies to.</summary>
/// <remarks>
/// Every field is part of the scope. A claim proven on one firmware revision says nothing about
/// another, and the whole point of recording the scope is that generated code can refuse to use a
/// claim outside it rather than assuming continuity.
/// </remarks>
public sealed record ClaimScope
{
    /// <summary>SMBIOS baseboard product, the exact board.</summary>
    public required string BaseboardProduct { get; init; }

    /// <summary>Baseboard revision, when it distinguishes hardware.</summary>
    public string? BaseboardVersion { get; init; }

    /// <summary>BIOS version the claim was established under.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>EC firmware version the claim was established under.</summary>
    public string? EcFirmwareVersion { get; init; }

    /// <summary>Controller or MCU firmware release the claim was established under.</summary>
    public string? ControllerFirmware { get; init; }
}

/// <summary>Byte order of a multi-byte field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Endianness>))]
public enum Endianness
{
    /// <summary>Not established, or not applicable to a single-byte field.</summary>
    Unspecified,

    /// <summary>Least significant byte first.</summary>
    Little,

    /// <summary>Most significant byte first.</summary>
    Big,
}

/// <summary>Whether a mutation made while establishing a claim was undone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RestorationResult>))]
public enum RestorationResult
{
    /// <summary>The claim was established without writing anything.</summary>
    NotApplicable,

    /// <summary>The original value was restored and confirmed by readback.</summary>
    RestoredVerified,

    /// <summary>A restore was written but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>Restoration failed. The resource is quarantined.</summary>
    RestoreFailed,
}

/// <summary>Where a claim came from.</summary>
/// <remarks>
/// Split into where the knowledge came from and what may be done with it, because those are different
/// questions. Learning a data address from another project's source is a fact about the hardware and
/// carries no licence obligation; copying that project's table is expression and does.
/// </remarks>
public sealed record ClaimProvenance
{
    /// <summary>Named source: a project, a specification, or a capture session.</summary>
    public required string Source { get; init; }

    /// <summary>Exact revision of that source.</summary>
    public string? SourceRevision { get; init; }

    /// <summary>How the knowledge relates to its source.</summary>
    public required ProvenanceKind Kind { get; init; }

    /// <summary>Licence of the source, when it has one.</summary>
    public string? SourceLicense { get; init; }
}

/// <summary>How a claim's knowledge relates to its source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProvenanceKind>))]
public enum ProvenanceKind
{
    /// <summary>A vendor specification or published documentation.</summary>
    OfficialDocumentation,

    /// <summary>Captured directly from the target hardware.</summary>
    IndependentCapture,

    /// <summary>A protocol fact read from another implementation and reimplemented.</summary>
    ProtocolFact,

    /// <summary>Behaviour observed from another product without reading its source.</summary>
    BehavioralObservation,

    /// <summary>Expression copied from another project. Requires a recorded licence decision.</summary>
    CopiedExpression,
}
