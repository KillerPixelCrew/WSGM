using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Automatic CEF work must distinguish a live Steam process from a fully
/// constructed Big Picture session. The former exists several seconds earlier on a
/// cold boot and coincided with a Steam hang while WSGM changed its library.</summary>
public class SteamUiReadinessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CanDriveAutomaticCef_WithoutBothSteamAndBigPicture_ReturnsFalse(
        bool steamRunning, bool bigPictureVisible)
    {
        Assert.False(SteamUiReadiness.CanDriveAutomaticCef(steamRunning, bigPictureVisible));
    }

    [Fact]
    public void CanDriveAutomaticCef_WithSteamAndBigPicture_ReturnsTrue()
    {
        Assert.True(SteamUiReadiness.CanDriveAutomaticCef(
            steamRunning: true, bigPictureVisible: true));
    }
}
