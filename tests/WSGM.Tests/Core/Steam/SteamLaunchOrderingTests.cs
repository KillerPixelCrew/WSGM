using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class SteamLaunchOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdStart_ReconcilesShimBeforeLaunching(bool steamInputManagement)
    {
        List<string> calls = [];
        var status = new SteamInputShimStatus(SteamInputShimState.Deployed, default, null);
        var expected = new LaunchResult(null, true, false);

        var result = Steam.ColdStart(steamInputManagement,
            (enabled, reason) =>
            {
                Assert.Equal(steamInputManagement, enabled);
                Assert.Equal("steam-cold-start", reason);
                calls.Add("shim");
                return status;
            },
            observed =>
            {
                Assert.Equal(status, observed);
                calls.Add("launch");
                return expected;
            });

        Assert.Same(expected, result);
        Assert.Equal(["shim", "launch"], calls);
    }

    [Fact]
    public void ColdStart_ReconcileFailureDoesNotLaunch()
    {
        List<string> calls = [];
        var failure = new IOException("fixture preparation failed");

        var thrown = Assert.Throws<IOException>(() => Steam.ColdStart(
            true,
            (_, _) =>
            {
                calls.Add("shim");
                throw failure;
            },
            _ =>
            {
                calls.Add("launch");
                return new LaunchResult(null, true, false);
            }));

        Assert.Same(failure, thrown);
        Assert.Equal(["shim"], calls);
    }
}
