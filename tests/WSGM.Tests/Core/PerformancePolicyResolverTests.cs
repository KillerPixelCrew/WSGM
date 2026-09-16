using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class PerformancePolicyResolverTests
{
    [Fact]
    public void ApplicationOverridesFallBackPerPropertyToGlobalValues()
    {
        var policy = new PerformancePolicy(
            new PerformanceValues(60, 1),
            [new PerformanceApplicationPolicy("steam:7", "game.exe", new PerformanceValues(null, 3))]);

        var (values, frameLayer, overlayLayer) = PerformancePolicyResolver.Resolve(
            policy,
            new PerformanceApplicationTarget("steam:7", 7, "game.exe"));

        Assert.Equal(new PerformanceValues(60, 3), values);
        Assert.Equal(PerformancePolicyLayer.Global, frameLayer);
        Assert.Equal(PerformancePolicyLayer.Application, overlayLayer);
    }

    [Fact]
    public void AutomaticEditUsesApplicationOnlyWhenAnOverrideAlreadyExists()
    {
        var target = new PerformanceApplicationTarget("steam:7", 7, "game.exe");
        var globalOnly = new PerformancePolicy(new PerformanceValues(60, 1), []);
        var withOverride = globalOnly with
        {
            Applications =
            [
                new PerformanceApplicationPolicy(
                    "steam:7",
                    "game.exe",
                    new PerformanceValues(45, null))
            ]
        };

        Assert.Equal(
            PerformancePersistenceTarget.Global,
            PerformancePolicyResolver.ResolveEditTarget(globalOnly, target));
        Assert.Equal(
            PerformancePersistenceTarget.Application,
            PerformancePolicyResolver.ResolveEditTarget(withOverride, target));
    }

}
