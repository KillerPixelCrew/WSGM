using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class ForegroundPumpLifetimeTests
{
    [Fact]
    public void LateCreationAfterStopIsDestroyedAndMissedJoinKeepsTheSignalAlive()
    {
        ForegroundPumpLifetime lifetime = new();
        Assert.Equal(0, lifetime.RequestStop());
        lifetime.CompleteJoin(false);
        List<nint> destroyed = [];
        Assert.False(lifetime.Publish(42, destroyed.Add));
        Assert.Equal(new nint[] { 42 }, destroyed);
        Assert.Equal(0, lifetime.Window);
        lifetime.SignalReady();
        lifetime.WaitReady(TimeSpan.Zero);
        lifetime.CompleteJoin(true);
    }

    [Fact]
    public void PublishedWindowRemainsOwnedUntilThePumpRetiresIt()
    {
        ForegroundPumpLifetime lifetime = new();
        List<nint> destroyed = [];
        Assert.True(lifetime.Publish(42, destroyed.Add));
        Assert.Equal(42, lifetime.RequestStop());
        lifetime.CompleteJoin(false);
        Assert.Equal(42, lifetime.Window);
        Assert.True(lifetime.Stopping);
        Assert.Empty(destroyed);
        lifetime.Retired();
        lifetime.SignalReady();
        Assert.Equal(0, lifetime.Window);
        lifetime.CompleteJoin(true);
    }
}
