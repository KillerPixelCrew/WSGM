using WSGM.Core;

namespace WSGM.Tests;

public sealed class DeelevationTests
{
    [Fact]
    public void SteamLaunchOptionsWrapTheHelperAndPreserveTheOriginalCommandPlaceholder()
        => Assert.Equal(
            "\"C:\\Users\\Player One\\WSGM.Deelevate.exe\" %command%",
            DeelevationCommand.SteamLaunchOptions("C:\\Users\\Player One\\WSGM.Deelevate.exe"));

    [Fact]
    public void SteamInputBlockLaunchOptionsSeparateTheWrappedCommandWithADoubleDash()
        => Assert.Equal(
            "\"C:\\Users\\Player One\\steam-input-lease.exe\" -- %command%",
            SteamInputLeaseCommand.SteamLaunchOptions("C:\\Users\\Player One\\steam-input-lease.exe"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SteamInputBlockLaunchOptionsRejectAMissingHelperPath(string helperPath)
        => Assert.Throws<ArgumentException>(
            () => SteamInputLeaseCommand.SteamLaunchOptions(helperPath));
}
