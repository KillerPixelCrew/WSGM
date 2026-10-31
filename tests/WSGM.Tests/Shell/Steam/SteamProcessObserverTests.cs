using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamProcessObserverTests
{
    [Theory]
    [InlineData("\"steamwebhelper.exe\" -steampid=1 --remote-debugging-port=8080",
        (int)SteamHelperRole.DebuggingBrowser)]
    [InlineData("\"steamwebhelper.exe\" -steampid=1 -uimode=4", (int)SteamHelperRole.Browser)]
    [InlineData("\"steamwebhelper.exe\" --type=renderer --remote-debugging-port=8080",
        (int)SteamHelperRole.Child)]
    [InlineData(null, (int)SteamHelperRole.Unknown)]
    public void HelpersAreToldApartByTheirCommandLine(string? commandLine, int role)
    {
        Assert.Equal((SteamHelperRole)role, SteamProcessObserver.Classify(commandLine));
    }
}
