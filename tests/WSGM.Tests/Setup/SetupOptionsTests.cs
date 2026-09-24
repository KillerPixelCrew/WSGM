using WSGM.Setup;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class SetupOptionsTests
{
    [Fact]
    public void QuietUpdate_IsTheUpdatersCommandLine()
    {
        var options = SetupOptions.Parse(["/quiet", "/update"], out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.True(options.Quiet);
        Assert.Equal(SetupMode.Update, options.Mode);
        Assert.True(SetupOptions.WantsQuiet(["/update", "/QUIET"]));
    }

    [Fact]
    public void ValuesAndDashSpellings_AreRead()
    {
        var options = SetupOptions.Parse(["-plugin=none", "/answers=C:\\a b\\answers.json", "/payload=D:\\p"], out _);

        Assert.NotNull(options);
        Assert.Equal("none", options.Plugin);
        Assert.Equal("C:\\a b\\answers.json", options.AnswersFile);
        Assert.Equal("D:\\p", options.PayloadDirectory);
    }

    [Theory]
    [InlineData("/verysilent")]
    [InlineData("/answers=")]
    [InlineData("/plugin")]
    public void UnknownOrEmptyArguments_AreRefused(string argument)
    {
        Assert.Null(SetupOptions.Parse([argument], out var error));
        Assert.Contains(argument, error, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallFlags_OnlyApplyToUninstall()
    {
        Assert.Null(SetupOptions.Parse(["/quiet", "/removedata"], out var error));
        Assert.NotNull(error);

        var options = SetupOptions.Parse(["/uninstall", "/removedata", "/keepcomponents"], out _);
        Assert.NotNull(options);
        Assert.True(options.RemoveData);
        Assert.True(options.KeepComponents);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\WSGM\\unins000.exe\" /SILENT", "C:\\Program Files\\WSGM\\unins000.exe", "/SILENT")]
    [InlineData("\"C:\\WSGM\\unins000.exe\"", "C:\\WSGM\\unins000.exe", "")]
    [InlineData("C:\\WSGM\\unins000.exe /x", "C:\\WSGM\\unins000.exe", "/x")]
    [InlineData("\"C:\\unterminated", "C:\\unterminated", "")]
    public void UninstallCommands_SplitIntoFileAndArguments(string command, string file, string arguments)
    {
        Assert.Equal((file, arguments), Registration.SplitCommand(command));
    }
}
