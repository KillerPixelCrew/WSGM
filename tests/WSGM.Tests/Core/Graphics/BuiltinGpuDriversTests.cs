using System.Text.Json;
using LibGPUDriverInteract;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class BuiltinGpuDriversTests
{
    [Theory]
    [InlineData(GpuVendor.Intel)]
    [InlineData(GpuVendor.Amd)]
    [InlineData(GpuVendor.Nvidia)]
    public void UnconfiguredDriversRemainEnabledBeforeAndAfterNormalization(GpuVendor vendor)
    {
        var config = new AppConfig();

        Assert.True(BuiltinGpuDrivers.Enabled(config, vendor));
        BuiltinGpuDrivers.Normalize(config);

        Assert.True(BuiltinGpuDrivers.Enabled(config, vendor));
    }

    [Theory]
    [InlineData(GpuVendor.Intel, "wsgm.gpu.intel", false)]
    [InlineData(GpuVendor.Intel, "wsgm.gpu.intel", true)]
    [InlineData(GpuVendor.Amd, "wsgm.gpu.amd", false)]
    [InlineData(GpuVendor.Amd, "wsgm.gpu.amd", true)]
    [InlineData(GpuVendor.Nvidia, "wsgm.gpu.nvidia", false)]
    [InlineData(GpuVendor.Nvidia, "wsgm.gpu.nvidia", true)]
    public void LegacyChoicesRemainEffectiveAfterNormalizationAndReload(GpuVendor vendor, string id, bool enabled)
    {
        var config = new AppConfig
        {
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = id, InstanceId = "custom", Enabled = enabled },
                new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }
            ]
        };

        Assert.Equal(enabled, BuiltinGpuDrivers.Enabled(config, vendor));
        BuiltinGpuDrivers.Normalize(config);
        BuiltinGpuDrivers.Normalize(config);
        var reloaded = ConfigRepair.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig))!;
        BuiltinGpuDrivers.Normalize(reloaded);

        Assert.Equal(enabled, BuiltinGpuDrivers.Enabled(config, vendor));
        Assert.Equal(enabled, BuiltinGpuDrivers.Enabled(reloaded, vendor));
        Assert.Equal("vendor.plugin", Assert.Single(reloaded.PluginInstances).PluginId);
        foreach (var other in BuiltinGpuDrivers.All.Where(driver => driver.Vendor != vendor))
        {
            Assert.True(BuiltinGpuDrivers.Enabled(reloaded, other.Vendor));
        }
    }

    [Fact]
    public void AnyEnabledLegacyInstanceKeepsItsVendorEnabled()
    {
        var config = new AppConfig
        {
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", InstanceId = "default", Enabled = false },
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", InstanceId = "custom", Enabled = true }
            ]
        };

        Assert.True(BuiltinGpuDrivers.Enabled(config, GpuVendor.Intel));
        BuiltinGpuDrivers.Normalize(config);

        Assert.True(config.GpuDrivers!.Intel);
        Assert.Empty(config.PluginInstances);
    }

    [Fact]
    public void ExplicitNativeChoicesTakePrecedenceOverLegacyChoicesAndSurviveReload()
    {
        var config = new AppConfig
        {
            GpuDrivers = new GpuDriverConfig { Intel = false, Amd = true, Nvidia = false },
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", Enabled = true },
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.amd", Enabled = false }
            ]
        };

        Assert.False(BuiltinGpuDrivers.Enabled(config, GpuVendor.Intel));
        Assert.True(BuiltinGpuDrivers.Enabled(config, GpuVendor.Amd));
        Assert.False(BuiltinGpuDrivers.Enabled(config, GpuVendor.Nvidia));
        BuiltinGpuDrivers.Normalize(config);
        var reloaded = ConfigRepair.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig))!;
        BuiltinGpuDrivers.Normalize(reloaded);

        Assert.False(BuiltinGpuDrivers.Enabled(reloaded, GpuVendor.Intel));
        Assert.True(BuiltinGpuDrivers.Enabled(reloaded, GpuVendor.Amd));
        Assert.False(BuiltinGpuDrivers.Enabled(reloaded, GpuVendor.Nvidia));
        Assert.Empty(reloaded.PluginInstances);
    }
}
