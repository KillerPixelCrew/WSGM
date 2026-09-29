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
    public void ABundleWrittenBeforeTheTestedHardwareListReadsItsListsAsEmpty()
    {
        // A 2.0.3 bundle.json has no testedHardware or replaces; reading it closed Settings.
        const string json = """
                            {"schemaVersion":1,"wsgmVersion":"2.0.3","plugins":[
                              {"id":"wsgm.device.msi.claw","name":"MSI Claw","version":"1.0.0","category":"wsgm.device",
                               "origin":"first-party","validation":"hardware-tested","contact":null,
                               "hardware":[{"baseboardManufacturer":"Micro-Star International Co., Ltd.","baseboardProduct":"MS-1T52"}],
                               "file":"claw.wsgmplugin","size":1,"sha256":"00"}]}
                            """;

        var plugin = Assert.Single(BundleManifest.Parse(Encoding.UTF8.GetBytes(json))!.Plugins);

        Assert.Empty(plugin.TestedHardware);
        Assert.Empty(plugin.Replaces);
        Assert.Empty(plugin.Capabilities);
        Assert.True(plugin.HardwareTestedOn(Claw));
    }

    [Fact]
    public void ExactTestedDevicePlugin_IsRecommendedWithItsComponents()
    {
        var bundle = Bundle(
            Device("claw", true, new HardwareMatchRule { BaseboardProduct = "MS-1T52" },
                [CapabilityRole.ControllerSource, CapabilityRole.FanMode]),
            Device("ally", true, new HardwareMatchRule { BaseboardProduct = "RC72LA" }),
            Common("ir"));

        var offers = PluginOffers.Compute(bundle, Claw, [], []);

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

        var offers = PluginOffers.Compute(bundle, Claw, [], []);

        Assert.Equal(["exact", "family"], offers.DeviceCandidates.Select(offer => offer.Plugin.Id));
        Assert.Equal("exact", offers.RecommendedDevice?.Plugin.Id);
    }

    [Fact]
    public void TwoEquallyGoodDevicePlugins_AskTheUserToChoose()
    {
        var bundle = Bundle(
            Device("a", true, new HardwareMatchRule { BaseboardProduct = "MS-1T52" }),
            Device("b", true, new HardwareMatchRule { SystemSku = "1T52.1" }));

        var offers = PluginOffers.Compute(bundle, Claw, [], []);

        Assert.Null(offers.RecommendedDevice);
        Assert.True(offers.NeedsDeviceChoice);
    }

    [Fact]
    public void UnsupportedHardware_GetsNoDeviceOffer_AndInstalledPluginsAreMarked()
    {
        var bundle = Bundle(Device("ally", true, new HardwareMatchRule { BaseboardProduct = "RC72LA" }),
            Common("ir"));

        var offers = PluginOffers.Compute(bundle, Claw, [], ["ir"]);

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

    [Fact]
    public void TestedHardware_LimitsTheTestedStatusToThoseBoards()
    {
        var claw = Device("claw", true, new HardwareMatchRule { BaseboardProduct = "MS-1T8K" }) with
        {
            Hardware =
            [
                new HardwareMatchRule { BaseboardProduct = "MS-1T52" },
                new HardwareMatchRule { BaseboardProduct = "MS-1T8K" }
            ],
            TestedHardware = ["MS-1T52"]
        };
        var bundle = Bundle(claw);

        var tested = PluginOffers.Compute(bundle, Claw, [], []).RecommendedDevice;
        var blind = PluginOffers.Compute(bundle, Claw with { BaseboardProduct = "MS-1T8K" }, [], []).RecommendedDevice;

        Assert.True(tested?.HardwareTested);
        Assert.False(blind?.HardwareTested);
        Assert.True(claw.HardwareTestedOn(null));
    }

    [Fact]
    public void GraphicsPlugins_AreOfferedForEveryPresentAdapterVendor()
    {
        var bundle = Bundle(Gpu("intel", "8086"), Gpu("nvidia", "10DE"), Gpu("amd", "1002"), Common("ir"));
        var hybrid = DisplayAdapterInventory.Parse(
            @"PCI\VEN_8086&DEV_7D55&SUBSYS_00000000&REV_08\3&11583659&0&10" + "\0"
            + @"PCI\VEN_10DE&DEV_2860&SUBSYS_00000000&REV_A1\4&1&0&0008" + "\0"
            + @"ROOT\DISPLAY\0000" + "\0\0");

        var offers = PluginOffers.Compute(bundle, Claw, hybrid, ["nvidia"]);

        Assert.Equal(["intel", "nvidia"], offers.Gpu.Select(offer => offer.Plugin.Id));
        Assert.True(offers.Gpu.Single(offer => offer.Plugin.Id == "nvidia").Installed);
        Assert.Empty(offers.Gpu.SelectMany(offer => offer.Components));
        Assert.Equal("amd", Assert.Single(offers.GpuNotForThisHardware).Id);
        Assert.Equal("ir", Assert.Single(offers.Common).Plugin.Id);
        Assert.Empty(offers.NotForThisHardware);
    }

    [Fact]
    public void GraphicsPlugins_AreNotOfferedWithoutAMatchingAdapter()
    {
        var bundle = Bundle(Gpu("intel", "8086"));
        IReadOnlyList<DisplayAdapterIdentity> amdOnly = [new DisplayAdapterIdentity("1002", "15BF", @"PCI\VEN_1002")];

        Assert.Empty(PluginOffers.Compute(bundle, Claw, [], []).Gpu);
        var offers = PluginOffers.Compute(bundle, Claw, amdOnly, []);
        Assert.Empty(offers.Gpu);
        Assert.Empty(offers.Common);
        Assert.Equal("intel", Assert.Single(offers.GpuNotForThisHardware).Id);
    }

    [Fact]
    public void BundleManifest_ReadsGraphicsAdaptersAndTreatsTheirAbsenceAsEmpty()
    {
        const string json = """
                            {"schemaVersion":1,"wsgmVersion":"2.1.0","plugins":[
                              {"id":"wsgm.gpu.intel","name":"Intel Graphics","version":"1.0.0","category":"wsgm.gpu",
                               "origin":"first-party","validation":"blind","capabilities":["VariableRefreshRate"],
                               "displayAdapters":[{"pciVendorId":"8086"}],"file":"intel.wsgmpkg","size":1,"sha256":"00"},
                              {"id":"wsgm.ir","name":"IR","version":"1.0.0","category":"wsgm.infrared",
                               "origin":"first-party","validation":"blind","file":"ir.wsgmpkg","size":1,"sha256":"01"}]}
                            """;

        var plugins = BundleManifest.Parse(Encoding.UTF8.GetBytes(json)).Plugins;

        Assert.True(plugins[0].IsGpu);
        Assert.Equal("8086", Assert.Single(plugins[0].DisplayAdapters).PciVendorId);
        Assert.Equal([CapabilityRole.VariableRefreshRate], plugins[0].Capabilities);
        Assert.False(plugins[1].IsGpu);
        Assert.Empty(plugins[1].DisplayAdapters);
        Assert.Equal("8086", Assert.Single(BundleManifest.Parse(Bundle(plugins[0]).ToUtf8Json()).Plugins)
            .DisplayAdapters[0].PciVendorId);
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

    private static BundledPlugin Gpu(string id, string vendor)
    {
        return new BundledPlugin
        {
            Id = id, Name = id, Version = "1.0.0", Category = BundledPlugin.GpuCategory, Origin = "first-party",
            Validation = "blind", Capabilities = [CapabilityRole.VariableRefreshRate],
            DisplayAdapters = [new BundledDisplayAdapter(vendor)], File = id + ".wsgmpkg", Sha256 = id
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
