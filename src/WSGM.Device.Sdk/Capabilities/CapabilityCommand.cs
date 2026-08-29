using System;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// A request to change or invoke one capability.
/// </summary>
/// <remarks>
/// The generation fields make a command refusable rather than merely late. A command authored against
/// descriptor generation 4 must not be applied after the plugin republished generation 5, because the
/// range it was validated against no longer exists — the plugin rejects it and WSGM re-issues from
/// the current descriptors. Without that, a stale slider position becomes a hardware write.
/// </remarks>
public sealed record CapabilityCommand
{
    /// <summary>Unique identifier for this command, used to correlate the outcome.</summary>
    public required Guid CommandId { get; init; }

    /// <summary>Capability being commanded.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Instance discriminator, matching the descriptor.</summary>
    public string? InstanceId { get; init; }

    /// <summary>The requested value, or null for an action.</summary>
    public CapabilityValue? RequestedValue { get; init; }

    /// <summary>Descriptor generation this command was authored against.</summary>
    public required long ExpectedDescriptorGeneration { get; init; }

    /// <summary>Device generation this command was authored against.</summary>
    public required long ExpectedCycleGeneration { get; init; }

    /// <summary>When the command stops being worth applying, in UTC.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>
/// How a command finished.
/// </summary>
/// <remarks>
/// Six outcomes rather than success and failure, because the three unhappy ones need different
/// handling. <see cref="Indeterminate"/> in particular is returned to the owning service and must
/// never be retried blindly for a
/// persistent write: the plugin does not know whether the write landed, and a second attempt could
/// double-apply it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CommandOutcome>))]
public enum CommandOutcome
{
    /// <summary>Validated and queued. Nothing has reached the hardware yet.</summary>
    Accepted,

    /// <summary>Written, with no readback available to confirm it.</summary>
    AppliedUnverified,

    /// <summary>Written and confirmed by an independent read.</summary>
    AppliedVerified,

    /// <summary>Refused before anything was written.</summary>
    Rejected,

    /// <summary>The deadline passed. Whether it was applied is unknown.</summary>
    TimedOut,

    /// <summary>Interrupted mid-operation. Whether it was applied is unknown.</summary>
    Indeterminate,
}

/// <summary>
/// The result of a capability command.
/// </summary>
public sealed record CapabilityCommandResult
{
    /// <summary>The command this result answers.</summary>
    public required Guid CommandId { get; init; }

    /// <summary>How it finished.</summary>
    public required CommandOutcome Outcome { get; init; }

    /// <summary>Why, when the outcome was not a clean apply.</summary>
    public CapabilityReason? Reason { get; init; }

    /// <summary>
    /// The value read back from hardware after applying.
    /// </summary>
    /// <remarks>
    /// Present only for <see cref="CommandOutcome.AppliedVerified"/>. This field, not the absence of
    /// an error, is what lets WSGM report a value as verified.
    /// </remarks>
    public CapabilityValue? ReadbackValue { get; init; }

    /// <summary>Whether the plugin restored the previous value after a failure.</summary>
    public RollbackResult Rollback { get; init; } = RollbackResult.NotRequired;

    /// <summary>When the command finished, in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>What happened to the previous value after a failed command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RollbackResult>))]
public enum RollbackResult
{
    /// <summary>Nothing was written, so nothing needed restoring.</summary>
    NotRequired,

    /// <summary>The previous value was restored and confirmed by readback.</summary>
    RestoredVerified,

    /// <summary>A restore was written but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>The restore failed. The resource is faulted and journalled for reconciliation.</summary>
    RestoreFailed,
}
