using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class OverlayPopupVisibilityTests
{
    // QUNS_ACCEPTS_NOTIFICATIONS — the plain-desktop state.
    private const int QunsAcceptsNotifications = 5;

    [Fact]
    public void SteamAndBorderlessNotificationStatesAllowThePopup()
    {
        Assert.True(OverlayPopupVisibility.AllowsPopup(0, QunsAcceptsNotifications));
        Assert.True(OverlayPopupVisibility.AllowsPopup(0, 2));
        Assert.True(OverlayPopupVisibility.AllowsPopup(0, 4));
        Assert.True(OverlayPopupVisibility.AllowsPopup(0, 7));
    }

    [Fact]
    public void ConfirmedExclusiveFullscreenSuppressesThePopup()
    {
        Assert.False(OverlayPopupVisibility.AllowsPopup(0, NativeMethods.QunsRunningD3dFullScreen));
    }

    [Fact]
    public void LockedOrInactiveSessionSuppressesThePopup()
    {
        Assert.False(OverlayPopupVisibility.AllowsPopup(0, NativeMethods.QunsNotPresent));
    }

    [Fact]
    public void FailedNotificationQuerySuppressesThePopup()
    {
        Assert.False(OverlayPopupVisibility.AllowsPopup(unchecked((int)0x80004005), QunsAcceptsNotifications));
    }
}
