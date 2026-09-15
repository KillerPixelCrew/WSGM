using WSGM.Core;

namespace WSGM.Tests;

public sealed class AppLauncherTests
{
    [Fact]
    public void LaunchResultIsAnImmutableValueSummary()
    {
        var result = new AppLauncher.LaunchResult(null, Started: false, ElevationDeclined: true);

        Assert.False(result.Started);
        Assert.True(result.ElevationDeclined);
        Assert.Null(result.Process);
    }

    [Theory]
    [InlineData("steam://open/bigpicture", true)]
    [InlineData("custom-scheme://action", true)]
    [InlineData("C:\\Games\\Steam.exe", false)]
    [InlineData("relative.exe", false)]
    public void ProtocolDetectionOnlyAcceptsUrls(string path, bool expected)
        => Assert.Equal(expected, AppLauncher.IsProtocol(path));

    [Fact]
    public void SafeDirectoryReturnsTheAbsoluteParentDirectory()
    {
        var path = Path.Combine("relative", "app.exe");

        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(path)), AppLauncher.SafeDirectory(path));
    }

    [Fact]
    public void SafeDirectoryHandlesInvalidPathsWithoutThrowing()
        => Assert.Equal("", AppLauncher.SafeDirectory("\0"));
}
