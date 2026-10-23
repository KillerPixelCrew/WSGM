using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ScreensaverInputActivityTests
{
    [Fact]
    public void OnlyRealChangesAdvanceActivityAndFailuresDoNotInventInput()
    {
        var activity = new ScreensaverInputActivity();
        var first = activity.Observe(lastInputTick: 100, now: 110)!;
        var idle = activity.Observe(lastInputTick: 100, now: 500)!;
        Assert.Equal(first.Revision, idle.Revision);
        Assert.Equal(400u, idle.AgeMilliseconds);
        Assert.Null(activity.Observe(lastInputTick: null, now: 600));
        var changed = activity.Observe(lastInputTick: 650, now: 660)!;
        Assert.Equal(first.Revision + 1, changed.Revision);
        Assert.Equal(10u, changed.AgeMilliseconds);
    }

    [Fact]
    public void SessionTickWrapPreservesElapsedTime()
    {
        var activity = new ScreensaverInputActivity();
        var sample = activity.Observe(lastInputTick: uint.MaxValue - 20, now: 30)!;
        Assert.Equal(51u, sample.AgeMilliseconds);
        var next = activity.Observe(lastInputTick: 25, now: 30)!;
        Assert.Equal(sample.Revision + 1, next.Revision);
        Assert.Equal(5u, next.AgeMilliseconds);
    }
}
