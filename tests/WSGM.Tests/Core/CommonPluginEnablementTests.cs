using WSGM.Core;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Tests.Core;

public sealed class CommonPluginEnablementTests
{
    private static readonly DisplayAdapterIdentity IntelAdapter = new("8086", "7D55", @"PCI\VEN_8086&DEV_7D55");
    private static readonly DisplayAdapterIdentity NvidiaAdapter = new("10DE", "2860", @"PCI\VEN_10DE&DEV_2860");

    private static readonly CommonInstalledPlugin IntelPackage =
        Package("wsgm.intel-gpu", PluginCategories.Gpu, "8086");

    private static readonly CommonInstalledPlugin IrPackage = Package("wsgm.ir", "wsgm.infrared");

    [Fact]
    public void AGraphicsPackageRunsByDefaultOnAMachineWithItsAdapter()
    {
        var desired = CommonPluginEnablement.Desired([], [IntelPackage, IrPackage], [IntelAdapter]);

        Assert.Equal([new PluginInstanceIdentity("wsgm.intel-gpu", "default")], desired);
    }

    [Fact]
    public void AGraphicsPackageNeverRunsWithoutItsAdapterEvenWhenEnabled()
    {
        CommonPluginInstanceConfig[] configured = [new() { PluginId = "wsgm.intel-gpu", Enabled = true }];

        Assert.Empty(CommonPluginEnablement.Desired([], [IntelPackage], [NvidiaAdapter]));
        Assert.Empty(CommonPluginEnablement.Desired(configured, [IntelPackage], [NvidiaAdapter]));
    }

    [Fact]
    public void AnExplicitlyDisabledGraphicsPackageStaysOff()
    {
        CommonPluginInstanceConfig[] configured = [new() { PluginId = "wsgm.intel-gpu", Enabled = false }];

        Assert.Empty(CommonPluginEnablement.Desired(configured, [IntelPackage], [IntelAdapter]));
    }

    [Fact]
    public void OtherPackagesStillNeedAnExplicitEnable()
    {
        CommonPluginInstanceConfig[] configured = [new() { PluginId = "wsgm.ir", InstanceId = "one", Enabled = true }];

        Assert.Empty(CommonPluginEnablement.Desired([], [IrPackage], [IntelAdapter]));
        Assert.Equal([new PluginInstanceIdentity("wsgm.ir", "one")],
            CommonPluginEnablement.Desired(configured, [IrPackage], []));
    }

    [Fact]
    public void AVendorIdMatchesWithoutCase()
    {
        var lower = Package("wsgm.intel-gpu", PluginCategories.Gpu, "8086");
        DisplayAdapterIdentity adapter = new("8086", "7d55", "fixture");

        Assert.True(CommonPluginEnablement.EnabledByDefault(lower.Manifest, [adapter]));
        Assert.False(CommonPluginEnablement.EnabledByDefault(IrPackage.Manifest, [adapter]));
    }

    private static CommonInstalledPlugin Package(string id, string category, params string[] vendors)
    {
        return new CommonInstalledPlugin(id + ".wsgmpkg", new PluginManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            Category = category,
            EntryAssembly = "Fixture.dll",
            EntryType = "Fixture.Plugin",
            DisplayAdapters = [.. vendors.Select(vendor => new DisplayAdapterMatch(vendor))]
        });
    }
}
