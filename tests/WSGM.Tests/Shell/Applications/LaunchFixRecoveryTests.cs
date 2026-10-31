using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class LaunchFixRecoveryTests
{
    private const string Helper = @"C:\WSGM\WSGM.Launch.exe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingAManualWrapperSnapshotsTheOriginalLaunchConfiguration(bool shortcut)
    {
        var details = shortcut
            ? new SteamAppDetails("", LaunchWrapperCommand.ShortcutTarget(Helper),
                LaunchWrapperCommand.ShortcutArguments(LaunchWrapperMode.Both, "\"D:\\Games\\game.exe\"", "-windowed"),
                @"D:\Games", "")
            : new SteamAppDetails(LaunchWrapperCommand.SteamLaunchOptions(Helper, LaunchWrapperMode.Both, "-windowed"),
                "", "", "", "");
        var snapshot = LaunchActionService.CaptureSnapshot(42, shortcut, details, null);
        Assert.NotNull(snapshot);
        Assert.Equal("-windowed", snapshot.OriginalLaunchOptions);
        if (shortcut)
        {
            Assert.Equal("\"D:\\Games\\game.exe\"", snapshot.OriginalTarget);
            Assert.Equal(@"D:\Games", snapshot.OriginalStartDir);
        }
    }

    [Fact]
    public void AWrapperWithoutARecoverableProgramRefusesANewRestorationRecord()
    {
        var arguments = LaunchWrapperCommand.ShortcutArguments(LaunchWrapperMode.Both, "game.exe", "");
        arguments = arguments[..arguments.IndexOf(" -- ", StringComparison.Ordinal)] + " -- ";
        var details = new SteamAppDetails("", LaunchWrapperCommand.ShortcutTarget(Helper), arguments, "", "");
        Assert.Null(LaunchActionService.CaptureSnapshot(42, true, details, null));
    }

    [Fact]
    public void AnExistingRestorationRecordKeepsTheFirstProgramAndArguments()
    {
        var original = new LaunchWrapperConfig
        {
            AppId = 42, IsShortcut = true, OriginalTarget = "original.exe", OriginalLaunchOptions = "-original"
        };
        var current = new SteamAppDetails("", "custom.exe", "-custom", "", "");
        var snapshot = LaunchActionService.CaptureSnapshot(42, true, current, original);
        Assert.NotNull(snapshot);
        Assert.Equal("original.exe", snapshot.OriginalTarget);
        Assert.Equal("-original", snapshot.OriginalLaunchOptions);
    }
}
