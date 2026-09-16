using System;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Command results shared by the Claw capabilities and the plugin.</summary>
/// <remarks>Each result is stamped with the time it is built, which is when the command finished.</remarks>
internal static class ClawResults
{
    public static CapabilityCommandResult Verified(CapabilityCommand command, CapabilityValue readback) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.AppliedVerified,
        ReadbackValue = readback,
        CompletedAt = DateTimeOffset.UtcNow
    };

    public static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail) => Rejected(command, new CapabilityReason(code, detail));

    public static CapabilityCommandResult Rejected(CapabilityCommand command, CapabilityReason reason) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.Rejected,
        Reason = reason,
        CompletedAt = DateTimeOffset.UtcNow
    };

    public static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        RollbackResult rollback) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(code, detail),
            Rollback = rollback,
            CompletedAt = DateTimeOffset.UtcNow
        };
}
