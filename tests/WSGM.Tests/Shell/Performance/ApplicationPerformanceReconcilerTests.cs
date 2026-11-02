using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ApplicationPerformanceReconcilerTests
{
    [Fact]
    public void AGlobalLimitIsNotTheGamesOwn()
    {
        // The reported bug: 20 W set by hand lands in Global, AutoTDP is switched on, and every wake
        // replayed that 20 W as an imposed limit, pausing AutoTDP until it was toggled.
        var layers = new ProfileLayers(new ProfileValues { SustainedWatts = 20 }, null);

        Assert.False(ApplicationPerformanceReconciler.SetByGame(layers));
    }

    [Fact]
    public void AGameProfileLimitIsTheGamesOwn()
    {
        var layers = new ProfileLayers(
            new ProfileValues { SustainedWatts = 20 },
            new ProfileValues { SustainedWatts = 15 });

        Assert.True(ApplicationPerformanceReconciler.SetByGame(layers));
    }

    [Fact]
    public void AGameProfileWithoutAPowerValueDoesNotOwnTheLimit()
    {
        var layers = new ProfileLayers(
            new ProfileValues { UnifiedWatts = 20, TdpUnified = true },
            new ProfileValues { FrameLimit = 60 });

        Assert.False(ApplicationPerformanceReconciler.SetByGame(layers));
    }
}
