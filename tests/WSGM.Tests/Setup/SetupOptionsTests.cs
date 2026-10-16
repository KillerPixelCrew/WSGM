using WSGM.Setup;

namespace WSGM.Tests.Setup;

public sealed class SetupOptionsTests
{
    [Fact]
    public void StartupReleasesItsOwnWorkingDirectoryAndPreservesRelativeInputPaths()
    {
        using SetupTestInstallation installation = new();
        var original = Environment.CurrentDirectory;
        var inherited = Path.Combine(installation.Root, "inherited-working-directory");
        Directory.CreateDirectory(inherited);
        try
        {
            Directory.SetCurrentDirectory(inherited);
            var moved = inherited + ".released";
            Assert.Throws<IOException>(() => Directory.Move(inherited, moved));

            var options = WSGM.Setup.Program.DetachWorkingDirectory(new SetupOptions
            {
                AnswersFile = "answers.json",
                PayloadDirectory = "payload"
            });

            Assert.Equal(Path.Combine(inherited, "answers.json"), options.AnswersFile);
            Assert.Equal(Path.Combine(inherited, "payload"), options.PayloadDirectory);
            Directory.Move(inherited, moved);
            Directory.Delete(moved);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

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
}
