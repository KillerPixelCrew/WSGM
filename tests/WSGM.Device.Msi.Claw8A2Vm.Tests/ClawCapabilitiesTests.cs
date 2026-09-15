using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Tests;
using static WSGM.Device.Tests.ClawCommands;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

[Collection("plugin-trace")]
public sealed class ClawCapabilitiesTests
{
    [Theory]
    [InlineData(8, 9, 20)]
    [InlineData(30, 37, 12)]
    [InlineData(8, 8, 37)]
    public async Task PairCommandChangesBothLimitsInFirmwareSafeOrder(int sustained, int boost, int target)
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, sustained);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, boost);
        wmi.AfterSetter = (_, _) => Assert.True(
            wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress) <= wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
        ClawA2VmPowerCapability power = new(wmi);
        var command = Command(CapabilityIds.PowerSustained, null,
            CapabilityValue.Integer(target)) with
        { ApplyPowerPair = true };
        var result = await power.ApplySustainedAsync(command, target, CancellationToken.None);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(target, result.ReadbackValue?.IntegerValue);
        Assert.Equal(target, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(target, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Fact]
    public async Task PairCommandFailedReadbackRestoresBothOriginalLimits()
    {
        FakeWmiTransport wmi = new();
        wmi.AfterSetter = (_, package) =>
        {
            if (package[0] == ClawHardwareFacts.PowerBoostAddress && package[1] == 12)
            { wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 13); }
        };
        ClawA2VmPowerCapability power = new(wmi);
        var command = Command(CapabilityIds.PowerSustained, null,
            CapabilityValue.Integer(12)) with
        { ApplyPowerPair = true };
        var result = await power.ApplySustainedAsync(command, 12, CancellationToken.None);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.RestoredVerified, result.Rollback);
        Assert.Equal(new PowerPair(30, 37, 0xC1), await power.ReadAsync(CancellationToken.None));
        Assert.Single(wmi.Writes, write => write.Package[0] == ClawHardwareFacts.PowerBoostAddress && write.Package[1] == 12);
    }

    [Fact]
    public void Encode_Lighting_ReplicatesThreeLogicalZonesAcrossNineProtocolIndices()
    {
        byte[] payload = ClawA2VmLightingCapability.Encode(new LightingState(
            60,
            0x112233,
            0x445566,
            0x778899));

        Assert.Equal(32, payload.Length);
        Assert.Equal([0x11, 0x22, 0x33], payload[5..8]);
        Assert.Equal([0x11, 0x22, 0x33], payload[14..17]);
        Assert.Equal([0x44, 0x55, 0x66], payload[17..20]);
        Assert.Equal([0x44, 0x55, 0x66], payload[26..29]);
        Assert.Equal([0x77, 0x88, 0x99], payload[29..32]);
    }

    [Fact]
    public async Task ApplyLighting_CancellationAfterPersistentWrite_RestoresPreviousProfile()
    {
        FakeMcuTransport mcu = new();
        ClawA2VmLightingCapability lighting = new(mcu);
        using CancellationTokenSource cancellation = new();
        CapabilityCommand command = Command(
            CapabilityIds.LightingBrightness,
            instanceId: null,
            CapabilityValue.Integer(75));
        mcu.AfterNextWrite = cancellation.Cancel;

        CapabilityCommandResult result = await lighting.ApplyAsync(
            command,
            current => current with { Brightness = 75 },
            cancellation.Token);
        LightingState restored = await lighting.ReadAsync(CancellationToken.None);

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.RestoredVerified, result.Rollback);
        Assert.Equal(50, restored.Brightness);
    }

    [Fact]
    public async Task ApplyLighting_PreservesUnknownProfileBytesAndVerifiesTheWholeReadback()
    {
        byte[] original = ClawA2VmLightingCapability.Encode(
            new LightingState(50, 0x112233, 0x445566, 0x778899));
        original[0] = 0xA5;
        original[3] = 0x7E;
        FakeMcuTransport mcu = new() { Profile = original };
        ClawA2VmLightingCapability lighting = new(mcu);
        CapabilityCommand command = Command(
            CapabilityIds.LightingBrightness,
            instanceId: null,
            CapabilityValue.Integer(75));

        CapabilityCommandResult result = await lighting.ApplyAsync(
            command,
            current => current with { Brightness = 75 },
            CancellationToken.None);

        byte[] expected = [.. original];
        expected[4] = 75;
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(expected, Assert.Single(mcu.ProfileWrites));
        Assert.Equal(expected, mcu.Profile);
    }

    [Fact]
    public async Task ApplyLighting_UnknownByteReadbackMismatch_RestoresExactRawProfile()
    {
        byte[] original = ClawA2VmLightingCapability.Encode(
            new LightingState(50, 0x112233, 0x445566, 0x778899));
        original[0] = 0xA5;
        original[3] = 0x7E;
        FakeMcuTransport mcu = new()
        {
            Profile = original,
            TransformNextWrite = payload =>
            {
                payload[0] ^= 0xFF;
                return payload;
            },
        };
        ClawA2VmLightingCapability lighting = new(mcu);
        CapabilityCommand command = Command(
            CapabilityIds.LightingBrightness,
            instanceId: null,
            CapabilityValue.Integer(75));

        CapabilityCommandResult result = await lighting.ApplyAsync(
            command,
            current => current with { Brightness = 75 },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.RestoredVerified, result.Rollback);
        Assert.Equal(2, mcu.ProfileWrites.Count);
        Assert.Equal(0xA5, mcu.ProfileWrites[0][0]);
        Assert.Equal(0x7E, mcu.ProfileWrites[0][3]);
        Assert.Equal(original, mcu.ProfileWrites[1]);
        Assert.Equal(original, mcu.Profile);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public async Task ApplyChargeLimit_WritesPercentageAndVerifiesReadback(int percent)
    {
        FakeWmiTransport wmi = new();
        ClawA2VmChargeLimitCapability chargeLimit = new(wmi);
        CapabilityCommand command = Command(
            CapabilityIds.ChargeLimit,
            instanceId: null,
            CapabilityValue.Integer(percent));

        CapabilityCommandResult result = await chargeLimit.ApplyAsync(
            command,
            percent,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(percent, result.ReadbackValue?.IntegerValue);
        Assert.Equal(percent, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    [Fact]
    public async Task ApplyCurveAsync_UsesMeasuredSixOffsetsAndPreservesUnknownBytes()
    {
        FakeWmiTransport wmi = new();
        ClawA2VmFanCapability fan = new(wmi);
        CapabilityCommand command = Command(
            CapabilityIds.FanCurve,
            instanceId: null,
            CapabilityValue.Curve(
            [
                new CurvePoint(0, 0),
                new CurvePoint(50, 40),
                new CurvePoint(60, 50),
                new CurvePoint(70, 60),
                new CurvePoint(80, 70),
                new CurvePoint(90, 80),
            ]));

        CapabilityCommandResult result = await fan.ApplyCurveAsync(
            command,
            command.RequestedValue!.CurveValue,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        byte[] dutyWrite = Assert.Single(
            wmi.Writes,
            write => write.Method == "Set_Fan" && write.Package[0] == 1).Package;
        byte[] temperatureWrite = Assert.Single(
            wmi.Writes,
            write => write.Method == "Set_Temperature" && write.Package[0] == 1).Package;
        Assert.Equal([0, 40, 50, 60, 70, 80], dutyWrite[2..8]);
        Assert.Equal(0xA1, dutyWrite[1]);
        Assert.Equal(0xA8, dutyWrite[8]);
        Assert.Equal(0, temperatureWrite[1]);
        Assert.Equal([50, 60, 70, 80, 90], temperatureWrite[4..9]);
        Assert.Equal(0xB2, temperatureWrite[2]);
        Assert.Equal(0xB3, temperatureWrite[3]);
    }

    /// The two fans share a heatsink and the firmware ramps them together, so one authored curve
    /// has to reach both channels or the pair describes a machine that does not exist.
    [Fact]
    public async Task ApplyCurveAsync_WritesTheSameCurveToBothFanChannels()
    {
        FakeWmiTransport wmi = new();
        ClawA2VmFanCapability fan = new(wmi);
        CapabilityCommand command = Command(
            CapabilityIds.FanCurve,
            instanceId: null,
            CapabilityValue.Curve(
            [
                new CurvePoint(0, 0),
                new CurvePoint(50, 40),
                new CurvePoint(60, 50),
                new CurvePoint(70, 60),
                new CurvePoint(80, 70),
                new CurvePoint(90, 80),
            ]));

        CapabilityCommandResult result = await fan.ApplyCurveAsync(
            command,
            command.RequestedValue!.CurveValue,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);

        // Only the six curve positions are compared. Every other byte in the package is firmware
        // data this write preserves per channel, and the two channels do not hold the same values
        // there — copying one channel's spare bytes onto the other is exactly the bug the
        // preserve-unknown-bytes test above exists to prevent.
        byte[] leftDuty = ChannelWrite(wmi, "Set_Fan", channel: 1);
        byte[] rightDuty = ChannelWrite(wmi, "Set_Fan", channel: 2);
        Assert.Equal(leftDuty[2..8], rightDuty[2..8]);

        byte[] leftTemperature = ChannelWrite(wmi, "Set_Temperature", channel: 1);
        byte[] rightTemperature = ChannelWrite(wmi, "Set_Temperature", channel: 2);
        Assert.Equal(leftTemperature[1], rightTemperature[1]);
        Assert.Equal(leftTemperature[4..9], rightTemperature[4..9]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScenarioMismatchOrCancellationRollsBackExactStateWithoutRetry(bool cancel)
    {
        FakeWmiTransport wmi = new();
        using CancellationTokenSource cancellation = new();
        wmi.AfterSetter = (method, package) =>
        {
            if (method != "Set_Data" || package[0] != ClawHardwareFacts.ScenarioAddress) { return; }
            wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 8);
            wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 9);
            if (package[1] == 0xC4)
            {
                if (cancel) { cancellation.Cancel(); }
                else { wmi.SetData(ClawHardwareFacts.ScenarioAddress, 0xC2); }
            }
        };
        ClawA2VmPowerCapability capability = new(wmi);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("sport")), "sport", cancellation.Token);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.RestoredVerified, result.Rollback);
        Assert.Equal(new PowerPair(30, 37, 0xC1), await capability.ReadAsync(CancellationToken.None));
        Assert.Single(wmi.Writes, write => write.Package[0] == ClawHardwareFacts.ScenarioAddress && write.Package[1] == 0xC4);
    }

    [Fact]
    public async Task UnsupportedScenarioIsRejectedWithoutWrites()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, 1);
        ClawA2VmPowerCapability capability = new(wmi);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("sport")), "sport", CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(0xC1, 0x81)]
    [InlineData(0xC4, 0x84)]
    public async Task InactiveScenarioClearsOnlyTheActiveBit(int initial, int expected)
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, initial);
        ClawA2VmPowerCapability capability = new(wmi);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("inactive")), "inactive", default);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(expected, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
    }

    [Fact]
    public async Task UnknownScenarioOnSupportedFirmwareIsRejectedWithoutWrites()
    {
        FakeWmiTransport wmi = new();
        ClawA2VmPowerCapability capability = new(wmi);
        var result = await capability.ApplyScenarioAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice("turbo")), "turbo", default);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
    }

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

    private static byte[] ChannelWrite(FakeWmiTransport wmi, string method, byte channel) =>
        Assert.Single(
            wmi.Writes,
            write => write.Method == method && write.Package[0] == channel).Package;
}
