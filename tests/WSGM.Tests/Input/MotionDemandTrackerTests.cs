using WSGM.Input;

namespace WSGM.Tests.Input;

public sealed class MotionDemandTrackerTests
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMilliseconds(80);

    [Fact]
    public void TheImuRequestIsLevelTriggered()
    {
        using MotionDemandTracker tracker = new(expiry: Expiry);
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.None);
        tracker.Observe(MotionSignal.ImuOn);
        tracker.Observe(MotionSignal.ImuOn);
        tracker.Observe(MotionSignal.ImuOff);

        Assert.False(tracker.Requested);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task AHeartbeatCountsUntilItExpiresAndAFreshOneExtendsIt()
    {
        using MotionDemandTracker tracker = new(expiry: Expiry);
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.ConsumerHeartbeat);
        Assert.True(tracker.Requested);

        await Task.Delay(Expiry / 2);
        tracker.Observe(MotionSignal.ConsumerHeartbeat);
        await Task.Delay(Expiry / 2 + TimeSpan.FromMilliseconds(10));
        // Half an expiry after the second beat: still fresh.
        Assert.True(tracker.Requested);

        await Task.Delay(Expiry * 3);
        Assert.False(tracker.Requested);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task TheImuKeepsMotionRequestedAfterTheHeartbeatExpires()
    {
        using MotionDemandTracker tracker = new(expiry: Expiry);

        tracker.Observe(MotionSignal.ImuOn);
        tracker.Observe(MotionSignal.ConsumerHeartbeat);
        await Task.Delay(Expiry * 3);

        Assert.True(tracker.Requested);
        tracker.Observe(MotionSignal.ImuOff);
        Assert.False(tracker.Requested);
    }

    [Fact]
    public void ResetForgetsEveryConsumerAndSaysSoOnce()
    {
        using MotionDemandTracker tracker = new(expiry: Expiry);
        List<bool> changes = [];
        tracker.Changed += changes.Add;

        tracker.Observe(MotionSignal.ImuOn);
        tracker.Observe(MotionSignal.ConsumerHeartbeat);
        tracker.Reset();
        tracker.Reset();

        Assert.False(tracker.Requested);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task DisposalStopsTheExpiryTimerAndFurtherSignals()
    {
        MotionDemandTracker tracker = new(expiry: Expiry);
        List<bool> changes = [];
        tracker.Changed += changes.Add;
        tracker.Observe(MotionSignal.ConsumerHeartbeat);

        tracker.Dispose();
        tracker.Observe(MotionSignal.ImuOn);
        await Task.Delay(Expiry * 3);

        Assert.Equal([true], changes);
    }
}
