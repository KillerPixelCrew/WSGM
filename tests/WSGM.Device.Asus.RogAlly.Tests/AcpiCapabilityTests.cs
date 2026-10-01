// SPDX-License-Identifier: MIT

using WSGM.Device.Asus.RogAlly.Tests.Fakes;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Asus.RogAlly.Tests;

public sealed class AcpiCapabilityTests
{
    [Fact]
    public void RaisingEveryLimitWritesFastFirst()
    {
        var order = AllyPowerCapability.WriteOrder(new AllyPowerState(10, 12, 15, 0), 25, 25, 25);

        Assert.Equal([AsusAcpiId.FastPower, AsusAcpiId.SlowPower, AsusAcpiId.SustainedPower],
            order.Select(item => item.Id));
    }

    [Fact]
    public void LoweringBelowTheCurrentSlowLimitWritesSustainedFirst()
    {
        var order = AllyPowerCapability.WriteOrder(new AllyPowerState(20, 25, 30, 0), 10, 10, 10);

        Assert.Equal([AsusAcpiId.SustainedPower, AsusAcpiId.SlowPower, AsusAcpiId.FastPower],
            order.Select(item => item.Id));
    }

    [Fact]
    public void UnknownLimitsFallBackToHhdOrder()
    {
        var order = AllyPowerCapability.WriteOrder(new AllyPowerState(null, null, null, null), 15, 15, 15);

        Assert.Equal([AsusAcpiId.FastPower, AsusAcpiId.SlowPower, AsusAcpiId.SustainedPower],
            order.Select(item => item.Id));
    }

    [Theory]
    [InlineData(10, 12, 15, 25, 25, 25)]
    [InlineData(20, 25, 30, 10, 10, 10)]
    [InlineData(20, 25, 30, 22, 24, 26)]
    [InlineData(10, 30, 30, 25, 26, 27)]
    public void EveryIntermediateStateKeepsTheInvariant(int s, int p, int f, int targetS, int targetP, int targetF)
    {
        var state = new[] { s, p, f };
        foreach (var (id, watts) in AllyPowerCapability.WriteOrder(new AllyPowerState(s, p, f, 0), targetS, targetP,
                     targetF))
        {
            state[id switch { AsusAcpiId.SustainedPower => 0, AsusAcpiId.SlowPower => 1, _ => 2 }] = watts;
            Assert.True(state[0] <= state[1] && state[1] <= state[2], $"{state[0]}/{state[1]}/{state[2]}");
        }

        Assert.Equal([targetS, targetP, targetF], state);
    }

    [Fact]
    public async Task UnifiedPairMovesAllThreeAndVerifies()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(22)), 22, 22,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(22, result.ReadbackValue!.IntegerValue);
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.SustainedPower));
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.SlowPower));
        Assert.Equal(22, acpi.Scalar(AsusAcpiId.FastPower));
    }

    [Fact]
    public async Task TheBoostValueGoesToSpptAndFpptTogether()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        _ = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(18)), 18, 20, CancellationToken.None);
        Assert.Equal((18, 20, 20), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));

        _ = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(28)), 28, 28, CancellationToken.None);
        Assert.Equal((28, 28, 28), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));
    }

    [Fact]
    public async Task ABoostCommandWritesThePairItCarriesAndReportsTheBoost()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(10), CapabilityIds.PowerBoost),
            10, 10, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(10, result.ReadbackValue!.IntegerValue);
        Assert.Equal((10, 10, 10), (acpi.Scalar(AsusAcpiId.SustainedPower), acpi.Scalar(AsusAcpiId.SlowPower),
            acpi.Scalar(AsusAcpiId.FastPower)));
    }

    [Fact]
    public async Task OutOfModelRangeIsRejectedBeforeAnyWrite()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc71l")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(35)), 35, 35, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(acpi.Writes);
    }

    [Fact]
    public async Task UnreadableLimitsAreReportedUnverified()
    {
        var acpi = new FakeAsusAcpi { ScalarsReadable = false };
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(20)), 20, 20,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Null(result.ReadbackValue);
    }

    [Fact]
    public async Task AReadbackMismatchLeavesTheWriteInPlace()
    {
        // As HC: the write is trusted. A readback that disagrees only means it cannot be verified; it is
        // never a reason to roll back or write again.
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.SlowPower };
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyLimitsAsync(Command(CapabilityValue.Integer(10)), 10, 10,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(10, acpi.Scalar(AsusAcpiId.SustainedPower));
    }

    [Fact]
    public async Task ScenarioWritesTheAsusModeValue()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);

        var result = await power.ApplyScenarioAsync(
            Command(CapabilityValue.Choice(Scenarios.Silent), CapabilityIds.Scenario),
            Scenarios.Silent, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(2, acpi.Scalar(AsusAcpiId.PerformanceMode));
        Assert.Equal((AsusAcpiId.PerformanceMode, 2u), acpi.Writes.Single());
    }

    [Fact]
    public async Task RestorePutsTheModeBackBeforeTheLimits()
    {
        var acpi = new FakeAsusAcpi();
        var power = new AllyPowerCapability(acpi, AllyModels.ById("rc72la")!, AllyFakeHardware.NoDelay);
        var original = power.Read();
        acpi.SetScalar(AsusAcpiId.PerformanceMode, 1);
        acpi.SetScalar(AsusAcpiId.SustainedPower, 25);
        acpi.SetScalar(AsusAcpiId.SlowPower, 25);
        acpi.SetScalar(AsusAcpiId.FastPower, 25);

        Assert.True(await power.RestoreAsync(original, CancellationToken.None));
        Assert.Equal(AsusAcpiId.PerformanceMode, acpi.Writes[0].Id);
        Assert.Equal(original, power.Read());
    }

    [Fact]
    public void ChargeLimitVerifiesAndRefusesTooLow()
    {
        var acpi = new FakeAsusAcpi();
        var charge = new AllyChargeLimitCapability(acpi);

        Assert.Equal(CommandOutcome.AppliedVerified,
            charge.Apply(Command(CapabilityValue.Integer(80), CapabilityIds.ChargeLimit), 80).Outcome);
        Assert.Equal(80, charge.Read());
        Assert.Equal(CommandOutcome.Rejected,
            charge.Apply(Command(CapabilityValue.Integer(20), CapabilityIds.ChargeLimit), 20).Outcome);
    }

    [Fact]
    public void AChargeLimitTheFirmwareDoesNotReportBackStaysWrittenAndIsNotRolledBack()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.ChargeLimit };
        acpi.SetScalar(AsusAcpiId.ChargeLimit, 100);
        var charge = new AllyChargeLimitCapability(acpi);

        var result = charge.Apply(Command(CapabilityValue.Integer(80), CapabilityIds.ChargeLimit), 80);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal([(AsusAcpiId.ChargeLimit, 80u)], acpi.Writes);
    }

    [Fact]
    public void FanCurvesEncodeLikeHc()
    {
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 0), new(40, 10), new(50, 20), new(60, 35), new(70, 50), new(80, 70), new(90, 90), new(100, 100)
        ];

        Assert.True(AllyFanCapability.TryEncode(points, out var curve, out _));
        Assert.Equal([30, 40, 50, 60, 70, 80, 90, 100, 0, 10, 20, 35, 50, 70, 90, 99], curve);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void FanCurvesNeedEightPoints(int count)
    {
        var points = Enumerable.Range(0, count).Select(index => new CurvePoint(30 + index * 5, 20)).ToArray();

        Assert.False(AllyFanCapability.TryEncode(points, out _, out _));
    }

    [Fact]
    public void FanCurvesRejectFallingDuties()
    {
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 50), new(40, 40), new(50, 50), new(60, 50), new(70, 50), new(80, 50), new(90, 50), new(100, 50)
        ];

        Assert.False(AllyFanCapability.TryEncode(points, out _, out _));
    }

    [Fact]
    public async Task FanCurveIsWrittenToEachFanAndVerified()
    {
        var acpi = new FakeAsusAcpi();
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 5), new(40, 10), new(50, 20), new(60, 35), new(70, 55), new(80, 75), new(90, 75), new(100, 75)
        ];

        var result = await fans.ApplyCurveAsync(Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.False(fans.HasMidFan);
        Assert.Equal([AsusAcpiId.CpuFanCurve, AsusAcpiId.GpuFanCurve], acpi.BufferWrites.Select(write => write.Id));
    }

    [Fact]
    public async Task FanCurveThatDoesNotEchoIsUnverifiedNotRolledBack()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.GpuFanCurve };
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        IReadOnlyList<CurvePoint> points =
        [
            new(30, 5), new(40, 10), new(50, 20), new(60, 35), new(70, 55), new(80, 75), new(90, 75), new(100, 75)
        ];

        var result = await fans.ApplyCurveAsync(Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(2, acpi.BufferWrites.Count);
    }

    [Fact]
    public async Task MidFanMustEchoBeforeAThreeChannelWriteIsVerified()
    {
        var acpi = new FakeAsusAcpi { IgnoreWritesTo = AsusAcpiId.MidFanCurve };
        acpi.SetCurve(AsusAcpiId.MidFanCurve, [.. AllyFanCapability.DefaultCpuCurve]);
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();
        var points = AllyFanCapability.Decode(AllyFanCapability.DefaultGpuCurve);

        var result = await fans.ApplyCurveAsync(
            Command(CapabilityValue.Curve(points), CapabilityIds.FanCurve), points, CancellationToken.None);

        Assert.True(fans.HasMidFan);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(3, acpi.BufferWrites.Count);
    }

    [Fact]
    public async Task AutomaticRestoresTheCapturedCurves()
    {
        var acpi = new FakeAsusAcpi();
        byte[] captured = [40, 45, 55, 63, 68, 74, 74, 74, 4, 8, 26, 34, 52, 74, 74, 74];
        var fans = new AllyFanCapability(acpi, AllyFakeHardware.NoDelay);
        fans.Probe();

        var result = await fans.ApplyAutomaticAsync(
            Command(CapabilityValue.Choice(FanModes.Automatic), CapabilityIds.FanMode),
            new AllyFanSnapshot(captured, captured, null, 0), CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(captured, acpi.Curve(AsusAcpiId.CpuFanCurve));
    }

    internal static CapabilityCommand Command(CapabilityValue value, string capabilityId = CapabilityIds.PowerSustained)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            RequestedValue = value,
            // WSGM carries the other limit on every power-limit write; these commands move both to one target.
            PairedPowerLimitWatts = capabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                ? value.IntegerValue
                : null,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = 1,
            Deadline = Deadline.After(TimeSpan.FromSeconds(10))
        };
    }
}
