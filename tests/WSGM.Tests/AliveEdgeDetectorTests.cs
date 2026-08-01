using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AliveEdgeDetectorTests
{
    [Fact]
    public void ReportsOnlyAnAliveToDeadTransition()
    {
        var detector = new AliveEdgeDetector();

        Assert.False(detector.Update(false));
        Assert.False(detector.Update(true));
        Assert.False(detector.Update(true));
        Assert.True(detector.Update(false));
        Assert.False(detector.Update(false));
        Assert.False(detector.Update(true));
        Assert.True(detector.Update(false));
    }
}
