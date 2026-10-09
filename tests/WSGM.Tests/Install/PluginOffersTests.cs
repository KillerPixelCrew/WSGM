using System.Text;
using LibHandheld.Contracts;
using WSGM.Install;
using WSGM.Testing;
using CapabilityRole = WSGM.Device.Sdk.Capabilities.CapabilityRole;

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
    public void LargeValidBundleParsesAndReadsFromFileWithoutLosingOutdatedEntries()
    {
        using TemporaryDirectory directory = new();
        var outdated = Enumerable.Range(0, 6000).Select(index => new OutdatedPlugin
        {
            Id = $"community.plugin-{index:D4}",
            Contact = "developer@example.test",
            Log = $"https://fixture.test/build/{index}/" + new string('x', 96)
        }).ToArray();
        BundleManifest bundle = new() { SchemaVersion = 1, WsgmVersion = "2.1.0", Outdated = outdated };
        var json = bundle.ToUtf8Json();
        Assert.True(json.Length > 1024 * 1024);
        var path = Path.Combine(directory.Root, "bundle.json");
        File.WriteAllBytes(path, json);

        var parsed = BundleManifest.Parse(json);
        var loaded = BundleManifest.TryRead(path);

        Assert.Equal(outdated, parsed.Outdated);
        Assert.NotNull(loaded);
        Assert.Equal(outdated, loaded.Outdated);
        Assert.Null(BundleManifest.TryRead(Path.Combine(directory.Root, "missing.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":2,"wsgmVersion":"2.1.0"}""")]
    public void EmptyMalformedAndUnsupportedBundlesAreStillRefused(string json)
    {
        using TemporaryDirectory directory = new();
        var path = Path.Combine(directory.Root, "bundle.json");
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => BundleManifest.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => BundleManifest.TryRead(path));
    }

    [Fact]
    public void GraphicsPlugins_AreOfferedForEveryPresentAdapterVendor()
    {
        var bundle = Bundle(Gpu("intel", "8086"), Gpu("nvidia", "10DE"), Gpu("amd", "1002"), Common("ir"));
        var hybrid = DisplayAdapterInventory.Parse(
            @"PCI\VEN_8086&DEV_7D55&SUBSYS_00000000&REV_08\3&11583659&0&10" + "\0"
                                                                            + @"PCI\VEN_10DE&DEV_2860&SUBSYS_00000000&REV_A1\4&1&0&0008" +
                                                                            "\0"
                                                                            + @"ROOT\DISPLAY\0000" + "\0\0");

        var offers = PluginOffers.Compute(bundle, Claw, hybrid, ["nvidia"]);

        Assert.Equal(["intel", "nvidia"], offers.Gpu.Select(offer => offer.Plugin.Id));
        Assert.True(offers.Gpu.Single(offer => offer.Plugin.Id == "nvidia").Installed);
        Assert.Empty(offers.Gpu.SelectMany(offer => offer.Components));
        Assert.Equal("amd", Assert.Single(offers.GpuNotForThisHardware).Id);
        Assert.Equal("ir", Assert.Single(offers.Common).Plugin.Id);
    }

    [Fact]
    public void GraphicsPlugins_AreNotOfferedWithoutAMatchingAdapter()
    {
        var bundle = Bundle(Gpu("intel", "8086"));
        IReadOnlyList<DisplayAdapterIdentity> amdOnly = [new("1002", "15BF", @"PCI\VEN_1002")];

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
