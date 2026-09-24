using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class RegistrationTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\WSGM\\unins000.exe\" /SILENT", "C:\\Program Files\\WSGM\\unins000.exe",
        "/SILENT")]
    [InlineData("\"C:\\WSGM\\unins000.exe\"", "C:\\WSGM\\unins000.exe", "")]
    [InlineData("C:\\WSGM\\unins000.exe /x", "C:\\WSGM\\unins000.exe", "/x")]
    [InlineData("\"C:\\unterminated", "C:\\unterminated", "")]
    public void UninstallCommands_SplitIntoFileAndArguments(string command, string file, string arguments)
    {
        Assert.Equal((file, arguments), Registration.SplitCommand(command));
    }
}
