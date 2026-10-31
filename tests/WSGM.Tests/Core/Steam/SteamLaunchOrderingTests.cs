using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class SteamLaunchOrderingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ColdStart_PreparesShimThenConfiguredDebuggingBeforeLaunching(
        bool steamInputManagement, bool cefEnabled)
    {
        List<string> calls = [];
        var status = new SteamInputShimStatus(SteamInputShimState.Deployed, default, null);
        var expected = new AppLauncher.LaunchResult(null, true, false);

        var result = Steam.ColdStart(steamInputManagement, "fixture-steam", cefEnabled,
            (enabled, reason) =>
            {
                Assert.Equal(steamInputManagement, enabled);
                Assert.Equal("steam-cold-start", reason);
                calls.Add("shim");
                return status;
            },
            (directory, enabled) =>
            {
                Assert.Equal("fixture-steam", directory);
                Assert.Equal(cefEnabled, enabled);
                calls.Add("debugging");
            },
            observed =>
            {
                Assert.Equal(status, observed);
                calls.Add("launch");
                return expected;
            });

        Assert.Same(expected, result);
        Assert.Equal(["shim", "debugging", "launch"], calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdStart_PreparationFailureDoesNotLaunch(bool shimFails)
    {
        List<string> calls = [];
        var failure = new IOException("fixture preparation failed");

        var thrown = Assert.Throws<IOException>(() => Steam.ColdStart(
            true, null, true,
            (_, _) =>
            {
                calls.Add("shim");
                if (shimFails)
                {
                    throw failure;
                }

                return default;
            },
            (_, _) =>
            {
                calls.Add("debugging");
                throw failure;
            },
            _ =>
            {
                calls.Add("launch");
                return new AppLauncher.LaunchResult(null, true, false);
            }));

        Assert.Same(failure, thrown);
        string[] expected = shimFails ? ["shim"] : ["shim", "debugging"];
        Assert.Equal(expected, calls);
    }
}
