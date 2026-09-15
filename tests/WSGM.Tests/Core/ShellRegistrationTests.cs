using WSGM.Core;

namespace WSGM.Tests;

public sealed class ShellRegistrationTests
{
    [Theory]
    [InlineData("WSGM.exe", "WSGM.exe")]
    [InlineData("C:\\Tools\\app.exe\t--argument", "C:\\Tools\\app.exe\t--argument")]
    [InlineData("\"C:\\Tools\\app.exe\"", "C:\\Tools\\app.exe")]
    public void ShellCommandParserUsesWinlogonSpaceSemantics(string command, string expected)
        => Assert.Equal(expected, ShellRegistration.ExtractExecutablePath(command));

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
