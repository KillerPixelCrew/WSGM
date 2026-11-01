using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class SteamAutostartServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASameNameUserSourceDoesNotDetermineTheMachineSourcesElevatedResult(bool machineStillEnabled)
    {
        const string steamExe = @"C:\Program Files (x86)\Steam\steam.exe";
        FakeAutostartSystem system = new()
        {
            Run =
            {
                [(SteamAutostartScope.User, false)] = new Dictionary<string, string> { ["Steam"] = steamExe },
                [(SteamAutostartScope.Machine, false)] = new Dictionary<string, string> { ["Steam"] = steamExe }
            }
        };
        var sources = SteamAutostartScanner.Scan(system, steamExe);
        var user = sources.Single(source => source.Scope is SteamAutostartScope.User);
        var machine = sources.Single(source => source.Scope is SteamAutostartScope.Machine);
        var initial = new SteamAutostartTakeoverResult([], [user], [machine]);
        if (!machineStillEnabled)
        {
            system.Approvals[(SteamAutostartScope.Machine, "Run", "Steam")] = [3, 0, 0, 0, 0, 0, 0, 0];
        }

        var remaining = SteamAutostartScanner.Scan(system, steamExe).Where(source => source.Enabled).ToArray();
        var result = SteamAutostartService.ReconcileElevatedResult(initial, remaining);

        Assert.Same(initial.Pending, result.Pending);
        Assert.Equal([user], result.Pending);
        Assert.False(result.Complete);
        Assert.Empty(system.Writes);
        if (machineStillEnabled)
        {
            Assert.Empty(result.Disabled);
            Assert.Equal([machine], result.NeedsElevation);
        }
        else
        {
            Assert.Equal([machine], result.Disabled);
            Assert.Empty(result.NeedsElevation);
        }
    }
}
