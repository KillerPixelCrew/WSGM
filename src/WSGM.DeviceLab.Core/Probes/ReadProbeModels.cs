using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Probes;

/// <summary>The allowlisted semantic family implemented by a reviewed read probe.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeFamily>))]
public enum ReadProbeFamily
{
    /// <summary>A provider, protocol, firmware, or native-library version read.</summary>
    Version,

    /// <summary>A WMI getter whose exact method and response shape are compiled into ProbeHost.</summary>
    WmiStatus,

    /// <summary>A known HID feature report read whose report ID and size are profile-scoped.</summary>
    HidFeature,

    /// <summary>A single allowlisted EC address read whose access path and address are profile-scoped.</summary>
    EmbeddedController,

    /// <summary>A controller mode or hardware profile read.</summary>
    ControllerMode,

    /// <summary>A current fan tachometer read.</summary>
    FanRpm,

    /// <summary>A current charge state or threshold read.</summary>
    ChargeState,

    /// <summary>Offline native-library version, architecture, hash, signer, or export inspection.</summary>
    NativeLibraryMetadata,
}

/// <summary>The scalar or byte representation expected from each probe repetition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeValueKind>))]
public enum ReadProbeValueKind
{
    /// <summary>A signed integral number.</summary>
    Integer,

    /// <summary>A true or false status.</summary>
    Boolean,

    /// <summary>A bounded UTF-8 string.</summary>
    Text,

    /// <summary>An exact or bounded byte sequence, represented as lower-case hexadecimal.</summary>
    Bytes,

    /// <summary>A dotted version string.</summary>
    Version,
}

/// <summary>How an independent observation must relate to the primary probe value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeCrossCheckKind>))]
public enum ReadProbeCrossCheckKind
{
    /// <summary>The independent observation must equal the primary value.</summary>
    Equal,

    /// <summary>The independent observation must be present and fall within its declared range.</summary>
    InRange,

    /// <summary>The independent observation must report the same normalized status.</summary>
    SameStatus,
}

/// <summary>Structural and semantic invariants for one read-probe response.</summary>
public sealed record ReadProbeResponseExpectation
{
    /// <summary>Expected representation.</summary>
    public required ReadProbeValueKind ValueKind { get; init; }

    /// <summary>Smallest encoded response length accepted.</summary>
    public required int MinimumLength { get; init; }

    /// <summary>Largest encoded response length accepted.</summary>
    public required int MaximumLength { get; init; }

    /// <summary>Allowlisted provider/protocol status values.</summary>
    public IReadOnlyList<int> AllowedStatusCodes { get; init; } = [0];

    /// <summary>Smallest numeric value accepted, when the representation is numeric.</summary>
    public long? MinimumValue { get; init; }

    /// <summary>Largest numeric value accepted, when the representation is numeric.</summary>
    public long? MaximumValue { get; init; }

    /// <summary>Whether all repetitions must return the same normalized value.</summary>
    public bool MustBeStable { get; init; } = true;
}

/// <summary>An independent read used to corroborate the primary response.</summary>
public sealed record ReadProbeCrossCheck
{
    /// <summary>Stable identifier of the compiled cross-check.</summary>
    public required string Id { get; init; }

    /// <summary>Required relation between the primary and independent values.</summary>
    public required ReadProbeCrossCheckKind Kind { get; init; }

    /// <summary>Smallest accepted independent numeric value for <see cref="ReadProbeCrossCheckKind.InRange"/>.</summary>
    public long? MinimumValue { get; init; }

    /// <summary>Largest accepted independent numeric value for <see cref="ReadProbeCrossCheckKind.InRange"/>.</summary>
    public long? MaximumValue { get; init; }
}

/// <summary>Named, versioned, hash-pinned catalog metadata for one reviewed read probe.</summary>
public sealed record ReadProbeMetadata
{
    /// <summary>Stable probe identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Probe contract version.</summary>
    public required int Version { get; init; }

    /// <summary>Exact normalized hardware-family identifier.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact endpoint identifier within that family.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Resource whose ownership must be checked before execution.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Allowlisted semantic family.</summary>
    public required ReadProbeFamily Family { get; init; }

    /// <summary>Authority source governing automatic versus explicit developer admission.</summary>
    public required DeviceLabOperationOrigin Origin { get; init; }

    /// <summary>SHA-256 of the locally installed ProbeHost assembly containing this entry point.</summary>
    public required string ImplementationSha256 { get; init; }

    /// <summary>Maximum calls per second, including repetitions and cross-checks.</summary>
    public required int MaximumReadsPerSecond { get; init; }

    /// <summary>Whole-probe deadline in milliseconds.</summary>
    public required int TimeoutMilliseconds { get; init; }

    /// <summary>Required number of repeated observations.</summary>
    public required int Repetitions { get; init; }

    /// <summary>Expected primary response invariants.</summary>
    public required ReadProbeResponseExpectation ExpectedResponse { get; init; }

    /// <summary>Required independent observation.</summary>
    public required ReadProbeCrossCheck CrossCheck { get; init; }

    /// <summary>Stable evidence stream written by a successful run.</summary>
    public required string EvidenceOutputId { get; init; }

    /// <summary>Whether the compiled getter requires an elevated disposable host.</summary>
    public bool RequiresElevation { get; init; }
}

/// <summary>Machine- and user-state supplied to probe admission.</summary>
public sealed record ReadProbeAdmissionContext
{
    /// <summary>Family observed by inventory and candidate matching.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Endpoint observed by inventory and candidate matching.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Whether the expected probe assembly is installed locally.</summary>
    public required bool IsLocallyInstalled { get; init; }

    /// <summary>SHA-256 calculated from that local assembly.</summary>
    public required string InstalledSha256 { get; init; }

    /// <summary>Whether Windows Developer Mode is enabled.</summary>
    public required bool DeveloperModeEnabled { get; init; }

    /// <summary>Whether the operator explicitly admitted this developer-origin probe now.</summary>
    public required bool ExplicitDeveloperAction { get; init; }

    /// <summary>Whether this is part of the unattended reviewed-catalog sweep.</summary>
    public required bool AutomaticSweep { get; init; }
}

/// <summary>Why a read probe is or is not admitted.</summary>
public sealed record ReadProbeAdmissionDecision
{
    /// <summary>Whether execution may proceed to the safety preflight.</summary>
    public required bool Allowed { get; init; }

    /// <summary>Stable rejection or success code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable decision.</summary>
    public required string Message { get; init; }
}

/// <summary>One bounded primary response and its independent observation.</summary>
public sealed record ReadProbeSample
{
    /// <summary>Representation returned by the typed profile.</summary>
    public required ReadProbeValueKind ValueKind { get; init; }

    /// <summary>Provider or protocol status.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Encoded primary response length.</summary>
    public required int Length { get; init; }

    /// <summary>Signed numeric form, when applicable.</summary>
    public long? NumericValue { get; init; }

    /// <summary>Normalized text, version, boolean, or lower-case hexadecimal form.</summary>
    public required string NormalizedValue { get; init; }

    /// <summary>Elapsed time for this repetition in milliseconds.</summary>
    public required int ElapsedMilliseconds { get; init; }

    /// <summary>Normalized independent observation.</summary>
    public required string CrossCheckValue { get; init; }

    /// <summary>Numeric independent observation, when applicable.</summary>
    public long? CrossCheckNumericValue { get; init; }
}

/// <summary>Execution state reported by the disposable host.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeHostStatus>))]
public enum ReadProbeHostStatus
{
    /// <summary>The host completed every bounded read.</summary>
    Completed,

    /// <summary>The typed profile could not open its resource because of access control.</summary>
    AccessDenied,

    /// <summary>The exact endpoint disappeared during execution.</summary>
    Disconnected,

    /// <summary>A compiled prerequisite was absent.</summary>
    PrerequisiteMissing,

    /// <summary>The profile rejected the request before opening the resource.</summary>
    Rejected,
}

/// <summary>Result document written once by a disposable ProbeHost process.</summary>
public sealed record ReadProbeHostResponse
{
    /// <summary>Response schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Probe identifier actually executed.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Probe version actually executed.</summary>
    public required int ProbeVersion { get; init; }

    /// <summary>Host execution state.</summary>
    public required ReadProbeHostStatus Status { get; init; }

    /// <summary>Bounded observations, one per requested repetition.</summary>
    public IReadOnlyList<ReadProbeSample> Samples { get; init; } = [];

    /// <summary>Structured failure detail without device identifiers or raw handles.</summary>
    public string? Error { get; init; }

    /// <summary>Must remain false for every read-only profile.</summary>
    public bool HardwareMutationObserved { get; init; }
}

/// <summary>Immutable invocation envelope consumed by one disposable ProbeHost process.</summary>
/// <remarks>
/// It contains no transport operation, address, method, report ID, library path, or arbitrary
/// parameter. Those remain compiled into the profile selected by <see cref="ProbeId"/>.
/// </remarks>
public sealed record ReadProbeHostRequest
{
    /// <summary>Request schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable compiled profile identifier.</summary>
    public required string ProbeId { get; init; }

    /// <summary>Exact compiled profile version.</summary>
    public required int ProbeVersion { get; init; }

    /// <summary>Exact family already matched by Device Lab.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact endpoint already matched by Device Lab.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Allowlisted semantic profile family.</summary>
    public required ReadProbeFamily Family { get; init; }

    /// <summary>SHA-256 of this installed ProbeHost.</summary>
    public required string ImplementationSha256 { get; init; }

    /// <summary>Cataloged rate ceiling that the compiled profile must not exceed.</summary>
    public required int MaximumReadsPerSecond { get; init; }

    /// <summary>Cataloged whole-process deadline.</summary>
    public required int TimeoutMilliseconds { get; init; }

    /// <summary>Cataloged repetition count.</summary>
    public required int Repetitions { get; init; }
}

/// <summary>Observed lifecycle of one disposable ProbeHost process.</summary>
public sealed record ReadProbeProcessOutcome
{
    /// <summary>Whether the process was started.</summary>
    public required bool Started { get; init; }

    /// <summary>Whether the supervisor killed it after its deadline.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Exit code, when the process reached an exit state.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether a result document was produced.</summary>
    public required bool ResultProduced { get; init; }

    /// <summary>Bounded stderr detail.</summary>
    public string? Error { get; init; }
}

/// <summary>End-to-end disposition of a supervised probe-host run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadProbeRunStatus>))]
public enum ReadProbeRunStatus
{
    /// <summary>The response passed every invariant.</summary>
    Accepted,

    /// <summary>Admission or safety preflight rejected execution.</summary>
    Rejected,

    /// <summary>The local host did not match its pinned hash.</summary>
    HashMismatch,

    /// <summary>The process could not be started.</summary>
    LaunchFailed,

    /// <summary>The process exited unexpectedly.</summary>
    HostCrashed,

    /// <summary>The process exceeded its deadline and was killed.</summary>
    HostHung,

    /// <summary>The typed endpoint rejected access.</summary>
    AccessDenied,

    /// <summary>The exact endpoint disconnected.</summary>
    Disconnected,

    /// <summary>The host result was missing or failed structural validation.</summary>
    MalformedResponse,
}

/// <summary>Classified result exposed to Device Lab callers.</summary>
public sealed record ReadProbeRunResult
{
    /// <summary>Stable run disposition.</summary>
    public required ReadProbeRunStatus Status { get; init; }

    /// <summary>Human-readable detail.</summary>
    public required string Message { get; init; }

    /// <summary>Validated response, present only when useful for evidence or diagnosis.</summary>
    public ReadProbeHostResponse? Response { get; init; }
}

/// <summary>Validation of a completed host response against catalog invariants.</summary>
public sealed record ReadProbeValidationResult
{
    /// <summary>Whether the response is usable as evidence.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Stable validation code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable validation detail.</summary>
    public required string Message { get; init; }
}
