using WSGM.Core;
using WSGM.Deelevate;

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

    [Fact]
    public async Task LaunchPayloadRoundTripsArgumentsEnvironmentAndWorkingDirectory()
    {
        var expected = new LaunchPayload(
            "C:\\Games\\Emulator",
            ["C:\\Games\\Emulator\\Ryujinx.exe", "--fullscreen", "value with spaces", "雪"],
            [KeyValuePair.Create("SteamAppId", "1234"), KeyValuePair.Create("EMPTY", "")]);
        await using var stream = new MemoryStream();

        await expected.WriteAsync(stream, CancellationToken.None);
        stream.Position = 0;
        var actual = await LaunchPayload.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.Arguments, actual.Arguments);
        Assert.Equal(expected.EnvironmentVariables, actual.EnvironmentVariables);
    }

    [Fact]
    public void ScheduledTaskUsesInteractiveTokenWithoutAnElevatedRunLevel()
    {
        var xml = ScheduledTaskLauncher.BuildTaskXml(
            "C:\\A&B\\WSGM.Deelevate.exe", "pipe<name>");

        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
        Assert.Contains("<Command>C:\\A&amp;B\\WSGM.Deelevate.exe</Command>", xml);
        Assert.Contains("<Arguments>--medium-child pipe&lt;name&gt;</Arguments>", xml);
    }
}
