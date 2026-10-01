using System;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>Builds the command results a plugin returns.</summary>
/// <remarks>Each result is stamped with the time it is built, which is when the command finished.</remarks>
public static class CommandResults
{
    /// <summary>A write whose readback matched.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="readback">The value read back.</param>
    /// <returns>An <see cref="CommandOutcome.AppliedVerified" /> result.</returns>
    public static CapabilityCommandResult Verified(CapabilityCommand command, CapabilityValue readback)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            ReadbackValue = readback,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A write the device accepted whose readback did not confirm it; the written value stands.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="written">The value written.</param>
    /// <returns>An <see cref="CommandOutcome.AppliedUnverified" /> result carrying the written value.</returns>
    public static CapabilityCommandResult Unverified(CapabilityCommand command, CapabilityValue written)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedUnverified,
            ReadbackValue = written,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A write the device accepted but that nothing can read back.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="detail">Why the write cannot be confirmed.</param>
    /// <returns>An <see cref="CommandOutcome.AppliedUnverified" /> result with that reason.</returns>
    public static CapabilityCommandResult Unverified(CapabilityCommand command, string detail)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedUnverified,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A command refused before anything was written.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="code">Why it was refused.</param>
    /// <param name="detail">The refusal in words.</param>
    /// <param name="retryable">Whether the same command may succeed later.</param>
    /// <returns>A <see cref="CommandOutcome.Rejected" /> result.</returns>
    public static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false)
    {
        return Rejected(command, new CapabilityReason(code, detail, retryable));
    }

    /// <summary>A command refused before anything was written.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>A <see cref="CommandOutcome.Rejected" /> result.</returns>
    public static CapabilityCommandResult Rejected(CapabilityCommand command, CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A command whose effect is unknown because it failed after the write began.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="code">Why the outcome is unknown.</param>
    /// <param name="detail">The failure in words.</param>
    /// <param name="rollback">What became of any restore.</param>
    /// <returns>An <see cref="CommandOutcome.Indeterminate" /> result.</returns>
    public static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        RollbackResult rollback)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(code, detail),
            Rollback = rollback,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
