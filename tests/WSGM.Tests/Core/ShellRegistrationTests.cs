using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class ShellRegistrationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("C:\\WSGM.exe --shell", "C:\\WSGM.exe")]
    [InlineData("  C:\\WSGM.exe --shell  ", "C:\\WSGM.exe")]
    [InlineData(@"""C:\Program Files\WSGM.exe"" --shell", @"C:\Program Files\WSGM.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("WSGM.exe", "WSGM.exe")]
    [InlineData("C:\\Tools\\app.exe\t--argument", "C:\\Tools\\app.exe\t--argument")]
    [InlineData(@"""C:\Tools\app.exe""", @"C:\Tools\app.exe")]
    public void ShellCommandParserReadsOnlyTheExecutableTokenWithWinlogonSpaceSemantics(string? command, string? expected)
        => Assert.Equal(expected, ShellRegistration.ExtractExecutablePath(command));
}
