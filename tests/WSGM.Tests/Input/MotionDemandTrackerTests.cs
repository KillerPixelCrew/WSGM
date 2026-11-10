using WSGM.Input;

namespace WSGM.Tests.Input;

public sealed class MotionDemandTrackerTests
{
    [Fact]
    public void AnImuRequestLatchesThroughLaterImuOffWrites()
    {
        using MotionDemandTracker tracker = new();
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.None);
        tracker.Observe(MotionSignal.ImuOff);
        Assert.False(tracker.Requested);

        tracker.Observe(MotionSignal.ImuOn);
        tracker.Observe(MotionSignal.ImuOff);
        tracker.Observe(MotionSignal.ImuOn);

        Assert.True(tracker.Requested);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void AHeartbeatRequestsMotionWithoutExpiring()
    {
        using MotionDemandTracker tracker = new();
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.ConsumerHeartbeat);
        tracker.Observe(MotionSignal.ConsumerHeartbeat);

        Assert.True(tracker.Requested);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void ResetForgetsEveryConsumerAndSaysSoOnce()
    {
        using MotionDemandTracker tracker = new();
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.ImuOn);
        tracker.Reset();
        tracker.Reset();
        tracker.Observe(MotionSignal.ConsumerHeartbeat);

        Assert.True(tracker.Requested);
        Assert.Equal([true, false, true], changes);
    }

    [Fact]
    public void DisposalIgnoresFurtherSignals()
    {
        MotionDemandTracker tracker = new();
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Dispose();
        tracker.Observe(MotionSignal.ImuOn);

        Assert.False(tracker.Requested);
        Assert.Empty(changes);
    }
}
