using WSGM.Core;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Tests;
using WSGM.Install;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

public sealed class PluginPackageManagerTests
{
    private static BundledPlugin Bundled(string id, string origin, string validation)
    {
        return new BundledPlugin
        {
            Id = id,
            Name = id,
            Version = "0.4.1",
            Category = "example.status",
            Origin = origin,
            Validation = validation,
            Contact = "dev@example.com",
            File = id + "-0.4.1.wsgmpkg",
            Sha256 = new string('a', 64)
        };
    }

    [Fact]
    public void AnInstalledFileTheBundleDidNotShip_IsALocalBuildWithItsVersionAndKind()
    {
        using TemporaryDirectory temporary = new();
        PluginPackageBuilders.WriteCommonFixture(temporary.Root, "test.local");

        var row = Assert.Single(PluginPackageManager.Rows(PluginPackageCatalog.Discover(temporary.Root), null,
            temporary.GetPath("bundled"), null));

        Assert.Equal(PluginPackageSection.Installed, row.Section);
        Assert.False(row.IsDevice);
        Assert.Equal(
        [
            new PluginBadge("Installed", PluginBadgeTone.Good), new PluginBadge("v1.0.0", PluginBadgeTone.Neutral),
            new PluginBadge("Integration", PluginBadgeTone.Neutral),
            new PluginBadge("Local build", PluginBadgeTone.Neutral)
        ], row.Badges);
        Assert.Equal(PluginPackageAction.Remove, row.Action);
    }

    [Fact]
    public void BundledPlugins_CarryTheirCuratedOriginAndValidationAsColouredBadges()
    {
        using TemporaryDirectory temporary = new();
        BundleManifest bundle = new()
        {
            SchemaVersion = 1,
            WsgmVersion = "2.0.0",
            Plugins =
            [
                Bundled("example.community", "community", "blind"),
                Bundled("example.tested", "first-party", "hardware-tested")
            ],
            Outdated = [new OutdatedPlugin { Id = "example.old", Contact = "old@example.com" }]
        };
        var catalog = PluginPackageCatalog.Discover(temporary.GetPath("plugins"));

        var rows = PluginPackageManager.Rows(catalog, bundle, temporary.GetPath("bundled"),
            PluginOffers.Compute(bundle, new DeviceIdentitySnapshot(), []));

        var community = rows.Single(row => row.Id == "example.community");
        Assert.Equal(PluginPackageSection.Available, community.Section);
        Assert.Contains(new PluginBadge("Community", PluginBadgeTone.Community), community.Badges);
        Assert.Contains(new PluginBadge("Blind", PluginBadgeTone.Warn), community.Badges);
        Assert.Equal("Developer: dev@example.com", community.Notice);
        var tested = rows.Single(row => row.Id == "example.tested");
        Assert.Contains(new PluginBadge("First-party", PluginBadgeTone.Accent), tested.Badges);
        Assert.Contains(new PluginBadge("Hardware-tested", PluginBadgeTone.Good), tested.Badges);
        var outdated = rows.Single(row => row.Id == "example.old");
        Assert.Equal(PluginPackageSection.Unavailable, outdated.Section);
        Assert.Equal(new PluginBadge("Outdated", PluginBadgeTone.Bad), outdated.Badges[0]);
        Assert.Contains("old@example.com", outdated.Notice, StringComparison.Ordinal);
    }
}
