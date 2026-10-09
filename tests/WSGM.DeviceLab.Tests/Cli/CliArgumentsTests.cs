using WSGM.DeviceLab.Cli;

namespace WSGM.DeviceLab.Tests.Cli;

public sealed class CliArgumentsTests
{
    [Fact]
    public void DeviceLabCli_RejectsAMisspelledRedactionFlag()
    {
        var error = DeviceLabCli.ValidateArguments(["inventory", "--out-dir", "capture", "--sharable"]);
        Assert.Contains("--sharable", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("pack")]
    [InlineData("validate")]
    [InlineData("glyph")]
    public async Task RetiredPackageCommands_AreRefusedWithoutLoadingCode(string command)
    {
        Assert.Equal(64, await DeviceLabCli.RunAsync([command], null));
    }
}
