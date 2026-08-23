using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamCustomLaunchCommandTests
{
    [Fact]
    public void Build_ExeWithArguments_UsesNativeSteamAndShortcutFields()
    {
        var fields = SteamCustomLaunchCommand.Build(
            "D:\\Launch Actions\\Tool.exe", "--profile \"Living Room\"");

        Assert.Equal(
            "\"D:\\Launch Actions\\Tool.exe\" --profile \"Living Room\" %command%",
            fields.LaunchOptions);
        Assert.Equal("\"D:\\Launch Actions\\Tool.exe\"", fields.ShortcutTarget);
        Assert.Equal("--profile \"Living Room\"", fields.ShortcutArguments);
    }

    [Theory]
    [InlineData("action.cmd")]
    [InlineData("action.BAT")]
    public void Build_BatchFile_UsesCommandProcessor(string file)
    {
        var fields = SteamCustomLaunchCommand.Build(
            $"D:\\Scripts\\{file}", "--wait", "C:\\Windows\\cmd.exe");

        Assert.Equal(
            $"\"C:\\Windows\\cmd.exe\" /d /s /c call \"D:\\Scripts\\{file}\" --wait %command%",
            fields.LaunchOptions);
        Assert.Equal("\"C:\\Windows\\cmd.exe\"", fields.ShortcutTarget);
        Assert.Equal($"/d /s /c call \"D:\\Scripts\\{file}\" --wait", fields.ShortcutArguments);
    }

    [Fact]
    public void Build_PowerShellFile_UsesExplicitNonInteractiveHost()
    {
        var fields = SteamCustomLaunchCommand.Build(
            "D:\\Scripts\\action.ps1", "-Profile Handheld", powerShell: "C:\\PowerShell.exe");

        Assert.Equal(
            "\"C:\\PowerShell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            "-File \"D:\\Scripts\\action.ps1\" -Profile Handheld %command%",
            fields.LaunchOptions);
        Assert.DoesNotContain("%command%", fields.ShortcutArguments);
    }

    [Fact]
    public void Build_MultilineArguments_RejectsThem()
        => Assert.Throws<ArgumentException>(() =>
            SteamCustomLaunchCommand.Build("D:\\Tool.exe", "first\r\nsecond"));

    [Fact]
    public void Build_UnsupportedExtension_RejectsIt()
        => Assert.Throws<ArgumentException>(() =>
            SteamCustomLaunchCommand.Build("D:\\Tool.com", ""));
}
