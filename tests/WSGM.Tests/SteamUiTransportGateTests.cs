using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SteamUiTransportGateTests
{
    [Fact]
    public void TransportShouldBeOpen_GameModeWithoutBigPictureWindow_HoldsTheTransportClosed()
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true, inGameMode: true, bigPictureReady: false));
    }

    [Fact]
    public void TransportShouldBeOpen_GameModeWithBigPictureWindow_Opens()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true, inGameMode: true, bigPictureReady: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportShouldBeOpen_DesktopMode_OpensOnTheMasterSwitchAlone(bool bigPictureReady)
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true, inGameMode: false, bigPictureReady: bigPictureReady));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransportShouldBeOpen_MasterSwitchOff_NeverOpens(bool inGameMode, bool bigPictureReady)
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: false, inGameMode: inGameMode, bigPictureReady: bigPictureReady));
    }
}
