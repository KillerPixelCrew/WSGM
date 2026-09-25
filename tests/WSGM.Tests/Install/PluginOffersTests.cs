using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Install;

namespace WSGM.Tests.Install;

public sealed class PluginOffersTests
{
    private static readonly DeviceIdentitySnapshot Claw = new()
    {
        BaseboardManufacturer = "Micro-Star International Co., Ltd.",
        BaseboardProduct = "MS-1T52",
        SystemSku = "1T52.1"
    };

    [Fact]
    public void ExactTestedDevicePlugin_IsRecommendedWithItsComponents()
    {
        var bundle = Bundle(
            Device("claw", true, new HardwareMatchRule { BaseboardProduct = "MS-1T52" },
                [CapabilityRole.ControllerSource, CapabilityRole.FanMode]),
            Device("ally", true, new HardwareMatchRule { BaseboardProduct = "RC72LA" }),
            Common("ir"));

        var offers = PluginOffers.Compute(bundle, Claw, []);

        Assert.Equal("claw", offers.RecommendedDevice?.Plugin.Id);
        Assert.Equal([SetupComponent.ControllerStack], offers.RecommendedDevice!.Components);
        Assert.Equal("ally", Assert.Single(offers.NotForThisHardware).Id);
        Assert.Equal("ir", Assert.Single(offers.Common).Plugin.Id);
        Assert.False(offers.NeedsDeviceChoice);
    }

    [Fact]
    public void ExactMatchOutranksAFamilyFallback_AndTestedOutranksBlind()
    {
        var bundle = Bundle(
            Device("family", true, new HardwareMatchRule
            {
                BaseboardManufacturer = "Micro-Star International Co., Ltd.", Fallback = true
            }),
            Device("exact", false, new HardwareMatchRule { BaseboardProduct = "MS-1T52" }));

        var offers = PluginOffers.Compute(bundle, Claw, []);

        Assert.Equal(["exact", "family"], offers.DeviceCandidates.Select(offer => offer.Plugin.Id));
        Assert.Equal("exact", offers.RecommendedDevice?.Plugin.Id);
    }

    [Fact]
    public void TwoEquallyGoodDevicePlugins_AskTheUserToChoose()
    {
        var bundle = Bundle(
            Device("a", true, new HardwareMatchRule { BaseboardProduct = "MS-1T52" }),
            Device("b", true, new HardwareMatchRule { SystemSku = "1T52.1" }));

        var offers = PluginOffers.Compute(bundle, Claw, []);

        Assert.Null(offers.RecommendedDevice);
        Assert.True(offers.NeedsDeviceChoice);
    }

    [Fact]
    public void UnsupportedHardware_GetsNoDeviceOffer_AndInstalledPluginsAreMarked()
    {
        var bundle = Bundle(Device("ally", true, new HardwareMatchRule { BaseboardProduct = "RC72LA" }),
            Common("ir"));

        var offers = PluginOffers.Compute(bundle, Claw, ["ir"]);

        Assert.Empty(offers.DeviceCandidates);
        Assert.Null(offers.RecommendedDevice);
        Assert.True(Assert.Single(offers.Common).Installed);
    }

    [Fact]
    public void BundleManifest_RoundTripsAndRefusesAnotherSchema()
    {
        var bundle = Bundle(Device("claw", true, new HardwareMatchRule { BaseboardProduct = "MS-1T52" },
            [CapabilityRole.HapticSink]));

        var read = BundleManifest.Parse(bundle.ToUtf8Json());

        Assert.Equal([CapabilityRole.HapticSink], Assert.Single(read.Plugins).Capabilities);
        Assert.Equal("claw", read.ByHash("ABC")?.Id);
        Assert.Throws<InvalidDataException>(() =>
            BundleManifest.Parse(Encoding.UTF8.GetBytes("""{"schemaVersion":2,"wsgmVersion":"2.0.0"}""")));
    }

    [Fact]
    public void Components_ComeOnlyFromControllerRoles()
    {
        Assert.Empty(SetupComponents.Required([CapabilityRole.FanMode, CapabilityRole.PowerSlowLimit]));
        Assert.Equal([SetupComponent.ControllerStack],
            SetupComponents.Required([CapabilityRole.MotionSource, CapabilityRole.HapticSink]));
    }

    private static BundleManifest Bundle(params BundledPlugin[] plugins)
    {
        return new BundleManifest { SchemaVersion = 1, WsgmVersion = "2.0.0", Plugins = plugins };
    }

    private static BundledPlugin Device(string id, bool tested, HardwareMatchRule rule,
        IReadOnlyList<CapabilityRole>? roles = null)
    {
        return new BundledPlugin
        {
            Id = id, Name = id, Version = "1.0.0", Category = BundledPlugin.DeviceCategory, Origin = "first-party",
            Validation = tested ? "hardware-tested" : "blind", Hardware = [rule], Capabilities = roles ?? [],
            File = id + ".wsgmpkg", Sha256 = "abc"
        };
    }

    private static BundledPlugin Common(string id)
    {
        return new BundledPlugin
        {
            Id = id, Name = id, Version = "1.0.0", Category = "wsgm.infrared", Origin = "first-party",
            Validation = "blind", File = id + ".wsgmpkg", Sha256 = "def"
        };
    }
}
