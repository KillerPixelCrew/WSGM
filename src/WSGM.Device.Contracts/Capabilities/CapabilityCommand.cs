using System;
using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Capabilities;

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

    /// <summary>
    /// Key that makes a retry of the same intent safe to apply once.
    /// </summary>
    /// <remarks>
    /// A retry after an indeterminate result is the case this exists for: the plugin can recognise
    /// that it already applied this exact intent rather than writing twice.
    /// </remarks>
    public required string IdempotencyKey { get; init; }

    /// <summary>Capability being commanded.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Instance discriminator, matching the descriptor.</summary>
    public string? InstanceId { get; init; }

    /// <summary>The requested value, or null for an action.</summary>
    public CapabilityValue? RequestedValue { get; init; }

    /// <summary>Descriptor generation this command was authored against.</summary>
    public required long ExpectedDescriptorGeneration { get; init; }

    /// <summary>Device generation this command was authored against.</summary>
    public required long ExpectedDeviceGeneration { get; init; }

    /// <summary>When the command stops being worth applying, in UTC.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>
/// How a command finished.
/// </summary>
/// <remarks>
/// Six outcomes rather than success and failure, because the three unhappy ones need different
/// handling. <see cref="Indeterminate"/> in particular must never be retried blindly for a
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

    /// <summary>The restore failed. The resource is quarantined and journalled.</summary>
    RestoreFailed,
}

/// <summary>
/// Helpers for reasoning about command outcomes.
/// </summary>
public static class CommandOutcomeRules
{
    /// <summary>
    /// Whether an outcome leaves the hardware in a state the plugin cannot describe.
    /// </summary>
    /// <param name="outcome">The outcome to classify.</param>
    /// <returns><see langword="true"/> when it is unknown whether the write landed.</returns>
    public static bool IsUncertain(CommandOutcome outcome) =>
        outcome is CommandOutcome.TimedOut or CommandOutcome.Indeterminate;

    /// <summary>
    /// Whether the same command may be retried automatically.
    /// </summary>
    /// <param name="outcome">The outcome of the previous attempt.</param>
    /// <param name="persistence">How long a write to this capability survives.</param>
    /// <returns><see langword="true"/> when an automatic retry is safe.</returns>
    /// <remarks>
    /// An uncertain outcome on a persistent write is never retried automatically. Unknown
    /// persistence counts as persistent: the point of the rule is that we do not know, and a second
    /// write to device-persistent storage is not something to guess about. A volatile write is safe
    /// to repeat because reapplying it converges on the same state either way.
    /// </remarks>
    public static bool MayRetryAutomatically(CommandOutcome outcome, CapabilityPersistence persistence)
    {
        if (!IsUncertain(outcome))
        {
            return outcome is CommandOutcome.Rejected;
        }

        return persistence is CapabilityPersistence.Volatile;
    }

    /// <summary>
    /// The state quality a command outcome justifies.
    /// </summary>
    /// <param name="outcome">The outcome of the command.</param>
    /// <returns>The strongest quality that outcome supports on its own.</returns>
    /// <remarks>
    /// This is the rule that keeps a successful IPC reply from becoming a verified hardware value:
    /// only an outcome carrying readback evidence reaches <see cref="HardwareStateQuality.Verified"/>.
    /// </remarks>
    public static HardwareStateQuality QualityFor(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.AppliedVerified => HardwareStateQuality.Verified,
        CommandOutcome.AppliedUnverified => HardwareStateQuality.Observed,
        CommandOutcome.Accepted => HardwareStateQuality.Unknown,
        CommandOutcome.Rejected => HardwareStateQuality.Unknown,
        _ => HardwareStateQuality.Unknown,
    };
}
