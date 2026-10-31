using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamProcessObserverTests
{
    [Theory]
    [InlineData("\"steamwebhelper.exe\" -steampid=1 --remote-debugging-port=8080",
        (int)SteamProcessObserver.HelperRole.DebuggingBrowser)]
    [InlineData("\"steamwebhelper.exe\" -steampid=1 -uimode=4", (int)SteamProcessObserver.HelperRole.Browser)]
    [InlineData("\"steamwebhelper.exe\" --type=renderer --remote-debugging-port=8080",
        (int)SteamProcessObserver.HelperRole.Child)]
    [InlineData(null, (int)SteamProcessObserver.HelperRole.Unknown)]
    public void HelpersAreToldApartByTheirCommandLine(string? commandLine, int role)
    {
        Assert.Equal((SteamProcessObserver.HelperRole)role, SteamProcessObserver.Classify(commandLine));
    }
}
