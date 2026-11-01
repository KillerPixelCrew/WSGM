using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginCatalogTests
{
    [Fact]
    public void DependenciesStartBeforeConsumersWithNumericVersionNormalization()
    {
        var provider = Manifest("provider") with { Version = "1.0" };
        var consumer = Manifest("consumer") with { Dependencies = [new PluginDependency("provider", "1.0.0", "2.0")] };
        var plan = CommonPluginDependencyPlan.Create([consumer, provider]);
        Assert.Equal(["provider", "consumer"], plan.Ordered.Select(package => package.Id));
        Assert.Empty(plan.Rejected);
    }

    [Fact]
    public void MissingAndIncompatibleDependenciesDoNotPreventIndependentPackages()
    {
        var provider = Manifest("provider") with { Version = "2.0" };
        var incompatible = Manifest("incompatible") with
        {
            Dependencies = [new PluginDependency("provider", "1.0", "2.0")]
        };
        var missing = Manifest("missing") with { Dependencies = [new PluginDependency("absent", "1.0")] };
        var dependent = Manifest("dependent") with { Dependencies = [new PluginDependency("missing", "1.0")] };
        var plan = CommonPluginDependencyPlan.Create([incompatible, missing, dependent, provider]);
        Assert.Equal("provider", Assert.Single(plan.Ordered).Id);
        Assert.Equal(3, plan.Rejected.Count);
    }

    [Fact]
    public void CyclesAndDuplicateIdentitiesAreRejectedWithoutRemovingIndependentPackages()
    {
        var first = Manifest("first") with { Dependencies = [new PluginDependency("second", "1.0")] };
        var second = Manifest("second") with { Dependencies = [new PluginDependency("first", "1.0")] };
        var plan = CommonPluginDependencyPlan.Create([
            first, second, Manifest("duplicate"), Manifest("duplicate"), Manifest("independent")
        ]);
        Assert.Equal("independent", Assert.Single(plan.Ordered).Id);
        Assert.Equal(3, plan.Rejected.Count);
    }

    private static PluginManifest Manifest(string id)
    {
        return new PluginManifest
        {
            Id = id, Name = id, Version = "1.0", Category = "test.plugin", EntryAssembly = "Fixture.dll",
            EntryType = "Fixture.Plugin"
        };
    }
}
