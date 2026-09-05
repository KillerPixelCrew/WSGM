using WSGM.Device.Sdk.Capabilities;

using WSGM.Device.Tests;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class ClawResponseValidationTests
{
    [Theory]
    [InlineData(ClawHardwareFacts.PowerSustainedAddress, 4)]
    [InlineData(ClawHardwareFacts.PowerBoostAddress, 4)]
    [InlineData(ClawHardwareFacts.ScenarioAddress, 1)]
    public async Task PowerRejectsTruncatedResponses(byte address, int length)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse("Get_Data", address, new byte[length]);
        ClawA2VmPowerCapability power = new(wmi);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => power.ReadAsync(CancellationToken.None).AsTask());

        Assert.Contains("truncated", failure.Message);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(ClawHardwareFacts.FanCustomAddress)]
    [InlineData(ClawHardwareFacts.FanFullSpeedAddress)]
    public async Task FanSnapshotRejectsTruncatedFlags(byte address)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse("Get_Data", address, new byte[1]);
        ClawA2VmFanCapability fan = new(wmi);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fan.ReadSnapshotAsync(CancellationToken.None).AsTask());
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData("Get_Fan", 4)]
    [InlineData("Get_Temperature", 1)]
    public async Task FanTelemetryRejectsTruncatedResponses(string method, int length)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse(method, 0, new byte[length]);
        ClawA2VmFanCapability fan = new(wmi);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fan.ReadTelemetryAsync(CancellationToken.None).AsTask());
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task UnknownFanModeIsRejectedBeforeAnyTransportAccess()
    {
        FakeWmiTransport wmi = new();
        ClawA2VmFanCapability fan = new(wmi);
        CapabilityCommand command = new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = CapabilityIds.FanMode,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = 1,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(2),
        };

        CapabilityCommandResult result = await fan.ApplyModeAsync(command, "unknown", CancellationToken.None);

        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason!.Code);
        Assert.Equal(0, wmi.Reads);
        Assert.Empty(wmi.Writes);
    }
}
