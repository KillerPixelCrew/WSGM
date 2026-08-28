using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of what may reach hardware: generation checks first, then the
/// descriptor's own limits.
/// </summary>
public class CommandAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AValidCommand_IsAdmitted()
    {
        CommandAdmission.Result result = Evaluate(Command(18));

        Assert.True(result.Admitted, result.Reason?.Detail);
    }

    [Fact]
    public void ACommandAuthoredAgainstASupersededDescriptor_IsRefused()
    {
        // The range it was validated against no longer exists. A stale slider position must not
        // become a hardware write just because it arrived late.
        CommandAdmission.Result result = Evaluate(
            Command(18) with { ExpectedDescriptorGeneration = 3 },
            currentDescriptorGeneration: 4);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
        Assert.True(result.Reason.Retryable);
    }

    [Fact]
    public void ACommandForAPreviousDeviceGeneration_IsRefused()
    {
        CommandAdmission.Result result = Evaluate(
            Command(18) with { ExpectedDeviceGeneration = 6 },
            currentDeviceGeneration: 7);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
    }

    [Fact]
    public void GenerationIsCheckedBeforeTheValue()
    {
        // An out-of-range value on a superseded generation must report the generation, because
        // re-issuing against current descriptors is the fix - not clamping the value.
        CommandAdmission.Result result = Evaluate(
            Command(9999) with { ExpectedDescriptorGeneration = 3 },
            currentDescriptorGeneration: 4);

        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
    }

    [Fact]
    public void APassedDeadline_IsRefused()
    {
        CommandAdmission.Result result = Evaluate(
            Command(18) with { Deadline = Now.AddSeconds(-1) });

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason!.Code);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    public void AValueOutsideTheDeclaredRange_IsRefused(int watts)
    {
        CommandAdmission.Result result = Evaluate(Command(watts));

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason!.Code);
    }

    [Fact]
    public void AValueOffTheStepBoundary_IsRefused()
    {
        // Step is measured from the minimum, not from zero: 8-30 in steps of 3 means 8, 11, 14.
        CapabilityDescriptor stepped = PowerDescriptor() with { Step = 3 };

        Assert.False(CommandAdmission.Evaluate(Command(10), stepped, 4, 7, true, Now).Admitted);
        Assert.True(CommandAdmission.Evaluate(Command(11), stepped, 4, 7, true, Now).Admitted);
    }

    [Fact]
    public void AWriteToAReadOnlyCapability_IsRefused()
    {
        CapabilityDescriptor readOnly = PowerDescriptor() with { SupportsWrite = false };

        CommandAdmission.Result result = CommandAdmission.Evaluate(Command(18), readOnly, 4, 7, true, Now);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason!.Code);
    }

    [Fact]
    public void AMismatchedValueKind_IsRefused()
    {
        CapabilityCommand wrongShape = Command(18) with
        {
            RequestedValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Boolean,
                BooleanValue = true,
            },
        };

        Assert.False(Evaluate(wrongShape).Admitted);
    }

    [Fact]
    public void ACapabilityUnavailableOnBattery_IsRefusedOnBattery()
    {
        CapabilityDescriptor acOnly = PowerDescriptor() with { AvailableOnDc = false };

        CommandAdmission.Result result =
            CommandAdmission.Evaluate(Command(18), acOnly, 4, 7, onAcPower: false, Now);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.UnavailableOnPowerSource, result.Reason!.Code);
    }

    [Fact]
    public void ThatSameCapability_IsStillAdmittedOnAc()
    {
        CapabilityDescriptor acOnly = PowerDescriptor() with { AvailableOnDc = false };

        Assert.True(CommandAdmission.Evaluate(Command(18), acOnly, 4, 7, onAcPower: true, Now).Admitted);
    }

    [Fact]
    public void AChoiceOutsideTheDeclaredOptions_IsRefused()
    {
        CapabilityDescriptor mode = ChoiceDescriptor();
        CapabilityCommand command = Command(0) with
        {
            CapabilityId = "fan.mode",
            RequestedValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = "turbo",
            },
        };

        CommandAdmission.Result result = CommandAdmission.Evaluate(command, mode, 4, 7, true, Now);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason!.Code);
    }

    [Fact]
    public void ADeclaredChoice_IsAdmitted()
    {
        CapabilityCommand command = Command(0) with
        {
            CapabilityId = "fan.mode",
            RequestedValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = "auto",
            },
        };

        Assert.True(CommandAdmission.Evaluate(command, ChoiceDescriptor(), 4, 7, true, Now).Admitted);
    }

    [Fact]
    public void ANonMonotonicCurve_IsRefused()
    {
        // Not a preference: firmware interprets an out-of-order table unpredictably.
        CapabilityCommand command = CurveCommand([new CurvePoint(50, 40), new CurvePoint(40, 50)]);

        Assert.False(CommandAdmission.Evaluate(command, CurveDescriptor(), 4, 7, true, Now).Admitted);
    }

    [Fact]
    public void AMonotonicCurve_IsAdmitted()
    {
        CapabilityCommand command = CurveCommand(
            [new CurvePoint(0, 0), new CurvePoint(50, 40), new CurvePoint(80, 75)]);

        Assert.True(CommandAdmission.Evaluate(command, CurveDescriptor(), 4, 7, true, Now).Admitted);
    }

    [Fact]
    public void AnEmptyCurve_IsRefused()
    {
        Assert.False(CommandAdmission
            .Evaluate(CurveCommand([]), CurveDescriptor(), 4, 7, true, Now).Admitted);
    }

    [Fact]
    public void AnActionOnACapabilityThatDoesNotSupportOne_IsRefused()
    {
        CapabilityCommand action = Command(0) with { RequestedValue = null };

        CommandAdmission.Result result = Evaluate(action);

        Assert.False(result.Admitted);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason!.Code);
    }

    private static CommandAdmission.Result Evaluate(
        CapabilityCommand command,
        long currentDescriptorGeneration = 4,
        long currentDeviceGeneration = 7) =>
        CommandAdmission.Evaluate(
            command, PowerDescriptor(), currentDescriptorGeneration, currentDeviceGeneration, true, Now);

    private static CapabilityCommand Command(int watts) => new()
    {
        CommandId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        IdempotencyKey = "power.primary-limit:18",
        CapabilityId = "power.primary-limit",
        RequestedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = watts,
        },
        ExpectedDescriptorGeneration = 4,
        ExpectedDeviceGeneration = 7,
        Deadline = Now.AddSeconds(5),
    };

    private static CapabilityCommand CurveCommand(IReadOnlyList<CurvePoint> points) => Command(0) with
    {
        CapabilityId = "fan.curve",
        RequestedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Curve,
            CurveValue = points,
        },
    };

    private static CapabilityDescriptor PowerDescriptor() => new()
    {
        CapabilityId = "power.primary-limit",
        Role = CapabilityRole.PowerSustainedLimit,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
        SupportsRead = true,
        SupportsWrite = true,
        Minimum = 8,
        Maximum = 30,
        Step = 1,
        Unit = CapabilityUnit.Watt,
        Persistence = CapabilityPersistence.Volatile,
    };

    private static CapabilityDescriptor ChoiceDescriptor() => new()
    {
        CapabilityId = "fan.mode",
        Role = CapabilityRole.FanMode,
        ValueKind = CapabilityValueKind.Choice,
        Display = new CapabilityDisplay { Key = DisplayKey.FanMode },
        SupportsRead = true,
        SupportsWrite = true,
        Choices =
        [
            new CapabilityChoice("auto", new CapabilityDisplay { Key = DisplayKey.FanMode }),
            new CapabilityChoice("custom", new CapabilityDisplay { Key = DisplayKey.FanCurve }),
        ],
        Persistence = CapabilityPersistence.Volatile,
    };

    private static CapabilityDescriptor CurveDescriptor() => new()
    {
        CapabilityId = "fan.curve",
        Role = CapabilityRole.FanCurve,
        ValueKind = CapabilityValueKind.Curve,
        Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
        SupportsRead = true,
        SupportsWrite = true,
        Persistence = CapabilityPersistence.Volatile,
    };
}
