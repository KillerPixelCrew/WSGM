using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ScreensaverInputActivityTests
{
    [Fact]
    public void OnlyRealChangesAdvanceActivityAndFailuresDoNotInventInput()
    {
        var activity = new ScreensaverInputActivity();
        var first = activity.Observe(100, 110)!;
        var idle = activity.Observe(100, 500)!;
        Assert.Equal(first.Revision, idle.Revision);
        Assert.Equal(400u, idle.AgeMilliseconds);
        Assert.Null(activity.Observe(null, 600));
        var changed = activity.Observe(650, 660)!;
        Assert.Equal(first.Revision + 1, changed.Revision);
        Assert.Equal(10u, changed.AgeMilliseconds);
    }

    [Fact]
    public void SessionTickWrapPreservesElapsedTime()
    {
        var activity = new ScreensaverInputActivity();
        var sample = activity.Observe(uint.MaxValue - 20, 30)!;
        Assert.Equal(51u, sample.AgeMilliseconds);
        var next = activity.Observe(25, 30)!;
        Assert.Equal(sample.Revision + 1, next.Revision);
        Assert.Equal(5u, next.AgeMilliseconds);
    }
}
