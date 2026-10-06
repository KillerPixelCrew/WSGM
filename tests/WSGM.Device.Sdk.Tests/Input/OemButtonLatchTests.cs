using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Sdk.Tests.Input;

public sealed class OemButtonLatchTests
{
    [Fact]
    public void GuideAndQuickAccessExpireIndependentlyAtTheHoldBoundary()
    {
        OemButtonLatch latch = new();
        var now = DateTimeOffset.UtcNow;
        latch.Press(CanonicalButtons.Guide, now);
        latch.Press(CanonicalButtons.QuickAccess, now.AddMilliseconds(100));

        Assert.Equal(CanonicalButtons.Guide | CanonicalButtons.QuickAccess, latch.Current(now.AddMilliseconds(150)));
        Assert.Equal(CanonicalButtons.QuickAccess, latch.Current(now + OemButtonLatch.HoldDuration));
        Assert.Equal(CanonicalButtons.None, latch.Current(now.AddMilliseconds(100) + OemButtonLatch.HoldDuration));
    }

    [Fact]
    public void ASecondPressExtendsTheTapAndClearReleasesItImmediately()
    {
        OemButtonLatch latch = new();
        var now = DateTimeOffset.UtcNow;
        latch.Press(CanonicalButtons.Guide, now);
        latch.Press(CanonicalButtons.Guide, now.AddMilliseconds(100));
        Assert.Equal(CanonicalButtons.Guide, latch.Current(now + OemButtonLatch.HoldDuration));

        latch.Clear();

        Assert.Equal(CanonicalButtons.None, latch.Current(now.AddMilliseconds(150)));
    }
}
