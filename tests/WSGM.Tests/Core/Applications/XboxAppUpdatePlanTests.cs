using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Which Xbox app build Game Mode needs, and which offered build is installed.</summary>
public sealed class XboxAppUpdatePlanTests
{
    [Fact]
    public void OnlyAnInstalledBuildOlderThanTheFirstDashboardBuildNeedsUpdating()
    {
        Assert.True(XboxAppUpdatePlan.NeedsUpdate(new Version(2611, 1001, 10, 0)));
        Assert.False(XboxAppUpdatePlan.NeedsUpdate(new Version(2612, 1001, 23, 0)));
        Assert.False(XboxAppUpdatePlan.NeedsUpdate(new Version(2701, 1, 0, 0)));
        Assert.False(XboxAppUpdatePlan.NeedsUpdate(null));
    }

    [Fact]
    public void TheNewestNeutralXboxBundleThatIsNewEnoughWins()
    {
        StorePackageOffer[] offers =
        [
            Offer("Microsoft.GamingApp_2611.1001.10.0_neutral_~_8wekyb3d8bbwe"),
            Offer("Microsoft.GamingApp_2612.1001.23.0_neutral_~_8wekyb3d8bbwe"),
            Offer("Microsoft.GamingApp_2612.1002.1.0_x64__8wekyb3d8bbwe"),
            Offer("Microsoft.GamingApp_2612.1001.30.0_neutral_~_8wekyb3d8bbwe"),
            Offer("Microsoft.GamingApp_2699.1.1.0_neutral_~_otherpublisher"),
            Offer("Microsoft.VCLibs.140.00_14.0.33519.0_neutral_~_8wekyb3d8bbwe")
        ];

        Assert.Equal("Microsoft.GamingApp_2612.1001.30.0_neutral_~_8wekyb3d8bbwe",
            XboxAppUpdatePlan.Choose(offers, new Version(2611, 1001, 10, 0))?.Moniker);
    }

    [Fact]
    public void NothingIsChosenWhenNoOfferIsNewEnoughOrNewerThanTheInstalledBuild()
    {
        Assert.Null(XboxAppUpdatePlan.Choose([Offer("Microsoft.GamingApp_2611.1001.10.0_neutral_~_8wekyb3d8bbwe")],
            new Version(2611, 1001, 1, 0)));
        Assert.Null(XboxAppUpdatePlan.Choose([Offer("Microsoft.GamingApp_2612.1001.23.0_neutral_~_8wekyb3d8bbwe")],
            new Version(2612, 1001, 23, 0)));
        Assert.Null(XboxAppUpdatePlan.Choose([Offer("Microsoft.GamingApp_bad_neutral_~_8wekyb3d8bbwe")], null));
    }

    private static StorePackageOffer Offer(string moniker)
    {
        return new StorePackageOffer(moniker, "id", 1, 1, new byte[32]);
    }
}
