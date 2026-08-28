using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of delta ordering and command deduplication — the two places where
/// an unordered channel could otherwise resurrect a value the device has moved past.
/// </summary>
public class CapabilityStateTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_UpdatesInOrder_KeepsTheLatest()
    {
        CapabilityStateTracker tracker = new(hostGeneration: 3);

        Assert.Equal(DeltaRejection.None, tracker.Apply(Delta(1, 18)));
        Assert.Equal(DeltaRejection.None, tracker.Apply(Delta(2, 20)));

        Assert.Equal(20, tracker.Latest("power.primary-limit")!.ObservedValue!.IntegerValue);
    }

    [Fact]
    public void Apply_ADelayedOlderSample_IsDiscarded()
    {
        // The high-rate channel does not promise ordering. An older sample overwriting a newer one
        // would restore a reading the device has already moved past, and the UI would command
        // against it.
        CapabilityStateTracker tracker = new(hostGeneration: 3);
        tracker.Apply(Delta(2, 20));

        Assert.Equal(DeltaRejection.OutOfOrder, tracker.Apply(Delta(1, 18)));
        Assert.Equal(20, tracker.Latest("power.primary-limit")!.ObservedValue!.IntegerValue);
    }

    [Fact]
    public void Apply_ARepeatedSequenceNumber_IsDiscarded()
    {
        CapabilityStateTracker tracker = new(hostGeneration: 3);
        tracker.Apply(Delta(2, 20));

        Assert.Equal(DeltaRejection.OutOfOrder, tracker.Apply(Delta(2, 25)));
        Assert.Equal(20, tracker.Latest("power.primary-limit")!.ObservedValue!.IntegerValue);
    }

    [Fact]
    public void Apply_AnUpdateFromASupersededHost_IsDiscarded()
    {
        // Sequence numbers restart with the host, so comparing them across the boundary would compare
        // two unrelated counters.
        CapabilityStateTracker tracker = new(hostGeneration: 4);

        Assert.Equal(DeltaRejection.StaleHostGeneration, tracker.Apply(Delta(99, 20, hostGeneration: 3)));
        Assert.Null(tracker.Latest("power.primary-limit"));
    }

    [Fact]
    public void ResetTo_ANewHostGeneration_DropsEverythingFromThePreviousOne()
    {
        CapabilityStateTracker tracker = new(hostGeneration: 3);
        tracker.Apply(Delta(5, 20));

        tracker.ResetTo(4);

        Assert.Null(tracker.Latest("power.primary-limit"));
        Assert.Equal(4, tracker.HostGeneration);
    }

    [Fact]
    public void ResetTo_LetsSequenceNumbersRestartFromZero()
    {
        CapabilityStateTracker tracker = new(hostGeneration: 3);
        tracker.Apply(Delta(500, 20));

        tracker.ResetTo(4);

        Assert.Equal(DeltaRejection.None, tracker.Apply(Delta(1, 18, hostGeneration: 4)));
        Assert.Equal(18, tracker.Latest("power.primary-limit")!.ObservedValue!.IntegerValue);
    }

    [Fact]
    public void Apply_TracksInstancesOfTheSameCapabilitySeparately()
    {
        // Two fans share a capability ID and differ only by instance. Collapsing them would make one
        // fan's RPM overwrite the other.
        CapabilityStateTracker tracker = new(hostGeneration: 3);

        tracker.Apply(Delta(1, 2400) with
        {
            State = State(2400, "fan.measured-rpm", 3) with { InstanceId = "left" },
        });
        tracker.Apply(Delta(2, 3100) with
        {
            State = State(3100, "fan.measured-rpm", 3) with { InstanceId = "right" },
        });

        Assert.Equal(2400, tracker.Latest("fan.measured-rpm", "left")!.ObservedValue!.IntegerValue);
        Assert.Equal(3100, tracker.Latest("fan.measured-rpm", "right")!.ObservedValue!.IntegerValue);
    }

    [Fact]
    public void Deduplicator_RecognisesARetryOfAnAlreadyAppliedIntent()
    {
        CommandDeduplicator deduplicator = new();
        deduplicator.Record("power.primary-limit:20", Result(CommandOutcome.AppliedVerified));

        Assert.True(deduplicator.TryGetCompleted("power.primary-limit:20", out CapabilityCommandResult? earlier));
        Assert.Equal(CommandOutcome.AppliedVerified, earlier!.Outcome);
    }

    [Theory]
    [InlineData(CommandOutcome.TimedOut)]
    [InlineData(CommandOutcome.Indeterminate)]
    public void Deduplicator_NeverRemembersAnUncertainOutcome(CommandOutcome outcome)
    {
        // The whole reason to retry an uncertain command is that nobody knows whether it landed.
        // Answering the retry with "already done" would assert exactly what is unknown.
        CommandDeduplicator deduplicator = new();
        deduplicator.Record("power.primary-limit:20", Result(outcome));

        Assert.False(deduplicator.TryGetCompleted("power.primary-limit:20", out _));
    }

    [Fact]
    public void Deduplicator_TreatsDifferentIntentsSeparately()
    {
        CommandDeduplicator deduplicator = new();
        deduplicator.Record("power.primary-limit:20", Result(CommandOutcome.AppliedVerified));

        Assert.False(deduplicator.TryGetCompleted("power.primary-limit:25", out _));
    }

    [Fact]
    public void Deduplicator_Clear_ForgetsEverything()
    {
        CommandDeduplicator deduplicator = new();
        deduplicator.Record("power.primary-limit:20", Result(CommandOutcome.AppliedVerified));

        deduplicator.Clear();

        Assert.False(deduplicator.TryGetCompleted("power.primary-limit:20", out _));
    }

    private static CapabilityStateDelta Delta(long sequence, int value, long hostGeneration = 3) =>
        new(sequence, State(value, "power.primary-limit", hostGeneration));

    private static CapabilityState State(int value, string capabilityId, long hostGeneration) => new()
    {
        CapabilityId = capabilityId,
        Available = true,
        Quality = HardwareStateQuality.Observed,
        ObservedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = value,
        },
        ObservedAt = Now,
        DescriptorGeneration = 1,
        DeviceGeneration = 7,
        HostGeneration = hostGeneration,
    };

    private static CapabilityCommandResult Result(CommandOutcome outcome) => new()
    {
        CommandId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Outcome = outcome,
        CompletedAt = Now,
    };
}
