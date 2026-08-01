using WSGM.Core;

namespace WSGM.Tests;

public sealed class CoreUtilityTests
{
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

    [Theory]
    [InlineData("", "")]
    [InlineData("/run", "/run")]
    [InlineData("/run /quiet", "/run")]
    [InlineData("  /run", "")]
    public void FirstTokenReturnsTheLeadingSpaceDelimitedToken(string arguments, string expected)
        => Assert.Equal(expected, ConsoleTool.FirstToken(arguments));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void QuoteUsesCommandLineToArgvWCompatibleEscaping(string argument, string expected)
        => Assert.Equal(expected, SelfElevation.Quote(argument));

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("C:\\WSGM.exe --shell", "C:\\WSGM.exe")]
    [InlineData("  C:\\WSGM.exe --shell  ", "C:\\WSGM.exe")]
    [InlineData("\"C:\\Program Files\\WSGM.exe\" --shell", "C:\\Program Files\\WSGM.exe")]
    [InlineData("\"unterminated", null)]
    public void ShellCommandParserReadsOnlyTheExecutableToken(string? command, string? expected)
        => Assert.Equal(expected, ShellRegistration.ExtractExecutablePath(command));
}
