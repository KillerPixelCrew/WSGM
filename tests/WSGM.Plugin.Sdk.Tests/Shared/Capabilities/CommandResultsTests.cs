using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Sdk.Tests.Capabilities;

public sealed class CommandResultsTests
{
    private static CapabilityCommand Command()
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = "charge.limit",
            Deadline = Deadline.Never
        };
    }

    [Fact]
    public void AnAcceptedWriteWithoutReadbackCarriesNoFault()
    {
        var command = Command();
        var before = DateTimeOffset.UtcNow;

        var result = CommandResults.Unverified(command);

        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.ReadbackValue);
        Assert.Null(result.Reason);
        Assert.Equal(RollbackResult.NotRequired, result.Rollback);
        Assert.InRange(result.CompletedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void AFailedConfirmationKeepsTheAcceptedWriteAndItsDiagnostic()
    {
        var result = CommandResults.Unverified(Command(), "The confirmation read failed.");

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.ReadbackValue);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, result.Reason?.Code);
        Assert.Equal("The confirmation read failed.", result.Reason?.Detail);
    }

    [Fact]
    public void AVerifiedWriteCarriesTheIndependentReadback()
    {
        var command = Command();
        var readback = CapabilityValue.Integer(80);

        var result = CommandResults.Verified(command, readback);

        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Same(readback, result.ReadbackValue);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void APreDispatchRefusalAndAnUncertainWriteKeepDifferentOutcomes()
    {
        var command = Command();
        var refused = CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, "No write budget.",
            true);
        var uncertain = CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
            "The write timed out.", RollbackResult.RestoreFailed);

        Assert.Equal(command.CommandId, refused.CommandId);
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.True(refused.Reason?.Retryable);
        Assert.Null(refused.ReadbackValue);
        Assert.Equal(RollbackResult.NotRequired, refused.Rollback);
        Assert.Equal(command.CommandId, uncertain.CommandId);
        Assert.Equal(CommandOutcome.Indeterminate, uncertain.Outcome);
        Assert.Equal(RollbackResult.RestoreFailed, uncertain.Rollback);
        Assert.Null(uncertain.ReadbackValue);
        Assert.False(uncertain.Reason?.Retryable);
    }
}
