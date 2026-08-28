using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of state quality, freshness, and command-outcome handling — the
/// rules that keep WSGM from showing a value the hardware never took.
/// </summary>
public class CapabilityStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QualityFor_AnAcceptedCommand_ClaimsNothingAboutTheHardware()
    {
        // Accepted means queued. A UI that treated it as an observation would show a value before
        // anything reached the device.
        Assert.Equal(HardwareStateQuality.Unknown,
            CommandOutcomeRules.QualityFor(CommandOutcome.Accepted));
    }

    [Fact]
    public void QualityFor_AnAppliedButUnverifiedWrite_IsObservedNotVerified()
    {
        // A successful IPC reply is not a hardware readback. Only an independent read earns Verified.
        Assert.Equal(HardwareStateQuality.Observed,
            CommandOutcomeRules.QualityFor(CommandOutcome.AppliedUnverified));
    }

    [Fact]
    public void QualityFor_AVerifiedWrite_IsVerified()
    {
        Assert.Equal(HardwareStateQuality.Verified,
            CommandOutcomeRules.QualityFor(CommandOutcome.AppliedVerified));
    }

    [Theory]
    [InlineData(CommandOutcome.TimedOut)]
    [InlineData(CommandOutcome.Indeterminate)]
    public void QualityFor_AnUncertainOutcome_NeverClaimsAnObservation(CommandOutcome outcome)
    {
        Assert.Equal(HardwareStateQuality.Unknown, CommandOutcomeRules.QualityFor(outcome));
        Assert.True(CommandOutcomeRules.IsUncertain(outcome));
    }

    [Theory]
    [InlineData(CommandOutcome.TimedOut, CapabilityPersistence.DevicePersistent)]
    [InlineData(CommandOutcome.Indeterminate, CapabilityPersistence.DevicePersistent)]
    [InlineData(CommandOutcome.TimedOut, CapabilityPersistence.Unknown)]
    [InlineData(CommandOutcome.Indeterminate, CapabilityPersistence.Unknown)]
    public void MayRetryAutomatically_AnUncertainPersistentWrite_IsNeverRetried(
        CommandOutcome outcome,
        CapabilityPersistence persistence)
    {
        // The plugin does not know whether the write landed. Repeating it could double-apply to
        // device storage - and Unknown persistence counts as persistent precisely because the point
        // of the rule is that nobody established otherwise.
        Assert.False(CommandOutcomeRules.MayRetryAutomatically(outcome, persistence));
    }

    [Theory]
    [InlineData(CommandOutcome.TimedOut)]
    [InlineData(CommandOutcome.Indeterminate)]
    public void MayRetryAutomatically_AnUncertainVolatileWrite_MayBeRetried(CommandOutcome outcome)
    {
        // Reapplying a volatile value converges on the same state whether or not the first attempt
        // landed, so a retry costs nothing.
        Assert.True(CommandOutcomeRules.MayRetryAutomatically(outcome, CapabilityPersistence.Volatile));
    }

    [Fact]
    public void Evaluate_AFreshObservationInTheCurrentGeneration_StaysUsable()
    {
        CapabilityState state = Observed(Now.AddSeconds(-1));

        CapabilityState result = CapabilityFreshness.Evaluate(
            state, FreshnessPolicy.Control, Now, currentDeviceGeneration: 7, currentHostGeneration: 3);

        Assert.Equal(HardwareStateQuality.Observed, result.Quality);
        Assert.True(CapabilityFreshness.CanCommand(result));
    }

    [Fact]
    public void Evaluate_AnExpiredObservation_GoesStaleAndBlocksCommands()
    {
        CapabilityState state = Observed(Now - FreshnessPolicy.Control.MaxAge - TimeSpan.FromSeconds(1));

        CapabilityState result = CapabilityFreshness.Evaluate(
            state, FreshnessPolicy.Control, Now, 7, 3);

        Assert.Equal(HardwareStateQuality.Stale, result.Quality);
        Assert.Equal(CapabilityReasonCode.ObservationExpired, result.Reason!.Code);
        Assert.False(CapabilityFreshness.CanCommand(result));
    }

    [Fact]
    public void Evaluate_ADeviceGenerationChange_InvalidatesEvenABrandNewObservation()
    {
        // Age is irrelevant here: the handles and hardware state the observation described belong to
        // a device that no longer exists.
        CapabilityState state = Observed(Now);

        CapabilityState result = CapabilityFreshness.Evaluate(
            state, FreshnessPolicy.Control, Now, currentDeviceGeneration: 8, currentHostGeneration: 3);

        Assert.Equal(HardwareStateQuality.Stale, result.Quality);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
    }

    [Fact]
    public void Evaluate_AHostGenerationChange_InvalidatesTheObservation()
    {
        CapabilityState state = Observed(Now);

        CapabilityState result = CapabilityFreshness.Evaluate(
            state, FreshnessPolicy.Control, Now, currentDeviceGeneration: 7, currentHostGeneration: 4);

        Assert.Equal(HardwareStateQuality.Stale, result.Quality);
    }

    [Fact]
    public void Evaluate_AFaultedCapability_KeepsItsFaultRatherThanBecomingStale()
    {
        // Stale says "old"; faulted says "broken". Downgrading loses the stronger claim.
        CapabilityState state = Observed(Now.AddHours(-1)) with
        {
            Quality = HardwareStateQuality.Faulted,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted),
        };

        CapabilityState result = CapabilityFreshness.Evaluate(
            state, FreshnessPolicy.Telemetry, Now, 7, 3);

        Assert.Equal(HardwareStateQuality.Faulted, result.Quality);
    }

    [Fact]
    public void Evaluate_AnObservationThatWasNeverTimestamped_IsStale()
    {
        CapabilityState state = Observed(Now) with { ObservedAt = null };

        Assert.Equal(HardwareStateQuality.Stale,
            CapabilityFreshness.Evaluate(state, FreshnessPolicy.Control, Now, 7, 3).Quality);
    }

    [Fact]
    public void CanCommand_AnUnavailableCapability_IsRefusedEvenWhenItsValueIsFresh()
    {
        CapabilityState state = Observed(Now) with
        {
            Available = false,
            Reason = new CapabilityReason(CapabilityReasonCode.ResourceConflict),
        };

        Assert.False(CapabilityFreshness.CanCommand(state));
    }

    [Fact]
    public void TelemetryAgesFasterThanSettings()
    {
        // A fan RPM is stale within seconds; a charge limit changes only when someone changes it. One
        // global timeout would either spam a slow transport or lie about a fast-moving reading.
        Assert.True(FreshnessPolicy.Telemetry.MaxAge < FreshnessPolicy.Control.MaxAge);
        Assert.True(FreshnessPolicy.Control.MaxAge < FreshnessPolicy.Settings.MaxAge);
    }

    private static CapabilityState Observed(DateTimeOffset at) => new()
    {
        CapabilityId = "power.primary-limit",
        Available = true,
        Quality = HardwareStateQuality.Observed,
        ObservedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 18,
        },
        ObservedAt = at,
        DescriptorGeneration = 1,
        DeviceGeneration = 7,
        HostGeneration = 3,
    };
}
