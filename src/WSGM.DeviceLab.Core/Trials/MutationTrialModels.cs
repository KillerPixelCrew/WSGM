using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Trials;

/// <summary>Only capability families that may receive a reviewed bounded trial.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MutationTrialFamily>))]
public enum MutationTrialFamily
{
    /// <summary>One temporary power pair step followed by exact pair restoration.</summary>
    TemporaryPowerPair,

    /// <summary>One fan at current-or-higher safe duty followed by firmware-mode restoration.</summary>
    FanDuty,

    /// <summary>One low-amplitude rumble followed by guaranteed zero output.</summary>
    Rumble,

    /// <summary>One proven-volatile zone at low brightness.</summary>
    VolatileRgbZone,

    /// <summary>One controller mode change continuing across a known re-enumeration.</summary>
    ControllerMode,
}

/// <summary>Immutable repository-reviewed description of one mutation trial.</summary>
public sealed record MutationTrialMetadata
{
    /// <summary>Stable built-in trial identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Reviewed trial contract version.</summary>
    public required int Version { get; init; }

    /// <summary>SHA-256 of the compiled trial implementation.</summary>
    public required string ImplementationSha256 { get; init; }

    /// <summary>Exact device family gate.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact board gate.</summary>
    public required string BoardId { get; init; }

    /// <summary>Allowlisted exact firmware identities.</summary>
    public required IReadOnlyList<string> FirmwareIdentities { get; init; }

    /// <summary>Exact endpoint gate.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Only resource the experiment lease may cover.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Only capability family the trial exercises.</summary>
    public required MutationTrialFamily Family { get; init; }

    /// <summary>Exact module version whose behavior is being tested.</summary>
    public required int ModuleVersion { get; init; }

    /// <summary>Maximum number of hardware writes including rollback and emergency action.</summary>
    public required int MaximumWrites { get; init; }

    /// <summary>Plain-language bounded action sequence.</summary>
    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>Expected temporary physical effect.</summary>
    public required string ExpectedEffect { get; init; }

    /// <summary>Independent observation or readback proving the effect.</summary>
    public required string IndependentObservation { get; init; }

    /// <summary>Exact restoration operation.</summary>
    public required string Rollback { get; init; }

    /// <summary>Independent emergency safe action.</summary>
    public required string EmergencyAction { get; init; }

    /// <summary>Whole-trial deadline in milliseconds.</summary>
    public required int TimeoutMilliseconds { get; init; }

    /// <summary>Maximum transient retries for one bounded step.</summary>
    public required int MaximumRetries { get; init; }

    /// <summary>Minimum interval before the same trial may run again.</summary>
    public required int CooldownSeconds { get; init; }

    /// <summary>Whether rollback has already been verified on the exact target.</summary>
    public required bool RollbackVerified { get; init; }

    /// <summary>Whether the operation was proven device-volatile where that is mandatory.</summary>
    public required bool DeviceVolatile { get; init; }
}

/// <summary>Every field the local operator must review before authorization.</summary>
[Flags]
public enum MutationTrialReviewField
{
    /// <summary>No review evidence.</summary>
    None = 0,

    /// <summary>Exact family, board, firmware, and endpoint.</summary>
    Identity = 1 << 0,

    /// <summary>Action list and maximum writes.</summary>
    Actions = 1 << 1,

    /// <summary>Expected temporary effect.</summary>
    Effect = 1 << 2,

    /// <summary>Single experiment lease and resource.</summary>
    Lease = 1 << 3,

    /// <summary>Rollback and independent emergency action.</summary>
    Recovery = 1 << 4,

    /// <summary>Independent observation/readback.</summary>
    Observation = 1 << 5,

    /// <summary>Timeout, retry, and cooldown bounds.</summary>
    Bounds = 1 << 6,

    /// <summary>All mandatory review fields.</summary>
    All = Identity | Actions | Effect | Lease | Recovery | Observation | Bounds,
}

/// <summary>Receipt proving the operator reviewed the locally resolved metadata.</summary>
public sealed record MutationTrialReviewReceipt
{
    /// <summary>Exact trial ID typed by the operator; no generic yes flag is accepted.</summary>
    public required string ConfirmedTrialId { get; init; }

    /// <summary>Fields actually shown by the caller.</summary>
    public required MutationTrialReviewField ReviewedFields { get; init; }

    /// <summary>SHA-256 of the rendered metadata reviewed by the operator.</summary>
    public required string ReviewSha256 { get; init; }

    /// <summary>UTC time of local confirmation.</summary>
    public required DateTimeOffset ConfirmedAt { get; init; }
}

/// <summary>Current values pinned into one short-lived authorization.</summary>
public sealed record MutationTrialAuthorizationSnapshot
{
    /// <summary>Safety preflight granting one experiment lease.</summary>
    public required DeviceLabPreflightDecision Preflight { get; init; }

    /// <summary>Exact observed family.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Exact observed board.</summary>
    public required string BoardId { get; init; }

    /// <summary>Exact observed firmware identity.</summary>
    public required string FirmwareIdentity { get; init; }

    /// <summary>Exact observed endpoint.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Installed trial implementation hash.</summary>
    public required string InstalledSha256 { get; init; }

    /// <summary>Current module version.</summary>
    public required int ModuleVersion { get; init; }

    /// <summary>Hash of the independently read original state.</summary>
    public required string OriginalStateSha256 { get; init; }

    /// <summary>Whether a local interactive console is available.</summary>
    public required bool IsInteractive { get; init; }

    /// <summary>Whether stdin is redirected or otherwise unattended.</summary>
    public required bool IsUnattended { get; init; }

    /// <summary>Whether CI markers are present.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Whether another mutation trial is already active.</summary>
    public required bool NestedTrialActive { get; init; }

    /// <summary>Last completion time for this trial, when known.</summary>
    public DateTimeOffset? LastCompletedAt { get; init; }

    /// <summary>Current UTC time.</summary>
    public required DateTimeOffset Now { get; init; }
}

/// <summary>Short-lived authorization pinned to all safety-relevant state.</summary>
public sealed record MutationTrialAuthorization
{
    /// <summary>Whether authorization was granted.</summary>
    public required bool Granted { get; init; }

    /// <summary>Stable decision code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable decision.</summary>
    public required string Message { get; init; }

    /// <summary>Fingerprint of metadata, preflight, generations, resource, and original state.</summary>
    public string? StateFingerprint { get; init; }

    /// <summary>Absolute authorization expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>One simulated interruption point in the transactional trial path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MutationTrialFaultPoint>))]
public enum MutationTrialFaultPoint
{
    /// <summary>No injected interruption.</summary>
    None,

    /// <summary>After original state was read but before durable planning.</summary>
    AfterSnapshot,

    /// <summary>After the planned journal record but before applying state.</summary>
    AfterPlannedJournal,

    /// <summary>After the applying record, where a crash makes write delivery ambiguous.</summary>
    AfterApplyingJournal,

    /// <summary>After the device write.</summary>
    AfterApply,

    /// <summary>After independent observation.</summary>
    AfterObservation,

    /// <summary>After rollback began.</summary>
    AfterRollbackStarted,

    /// <summary>After restoration was written but before readback.</summary>
    AfterRestore,

    /// <summary>After restoration was independently verified.</summary>
    AfterRestoreVerified,
}

/// <summary>Outcome of one transaction or fault-injection simulation.</summary>
public sealed record MutationTrialOutcome
{
    /// <summary>Independent compatibility dimensions.</summary>
    public required ProbeResult Result { get; init; }

    /// <summary>Named resource quarantined by failed or unverified restoration, if any.</summary>
    public string? QuarantinedResourceId { get; init; }

    /// <summary>Journal states durably expected before the interruption.</summary>
    public IReadOnlyList<JournalEntryStatus> JournalStates { get; init; } = [];
}
