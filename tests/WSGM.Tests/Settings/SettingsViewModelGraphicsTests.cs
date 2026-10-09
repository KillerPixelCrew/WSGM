using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsViewModelGraphicsTests
{
    [Fact]
    public void LegacyMigrationRequiresAnExplicitEnabledDriverAndKeepsCommonPlugins()
    {
        var config = new AppConfig
        {
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", InstanceId = "custom", Enabled = true },
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.amd", Enabled = false },
                new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }
            ]
        };
        BuiltinGpuDrivers.Normalize(config);
        Assert.True(config.GpuDrivers!.Intel);
        Assert.False(config.GpuDrivers.Amd);
        Assert.False(config.GpuDrivers.Nvidia);
        Assert.Equal("vendor.plugin", Assert.Single(config.PluginInstances).PluginId);
    }

    [Fact]
    public void ExplicitDisabledDriverIsNotEnabledByALegacyEntry()
    {
        var config = new AppConfig
        {
            GpuDrivers = new GpuDriverConfig(),
            PluginInstances = [new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", Enabled = true }]
        };
        BuiltinGpuDrivers.Normalize(config);
        Assert.False(config.GpuDrivers.Intel);
        Assert.Empty(config.PluginInstances);
    }

    [Fact]
    public async Task PackageRefreshPreservesTheSingleVendorDraft()
    {
        var model = Model(new AppConfig { GpuDrivers = new GpuDriverConfig() });
        var intel = Assert.Single(model.GraphicsDrivers);
        intel.Enabled = true;
        await model.LoadPluginPackagesAsync();
        await model.LoadPluginPackagesAsync();
        Assert.Same(intel, Assert.Single(model.GraphicsDrivers));
        var edit = Assert.Single(model.CaptureSaveRequest().CommonPluginEdits);
        Assert.Equal(("wsgm.gpu.intel", "default", true), (edit.PluginId, edit.InstanceId, edit.Enabled));
    }

    [Fact]
    public void GraphicsSaveChangesOnlyTheEditedVendorAndKeepsFreshSteamChoices()
    {
        var model = Model(new AppConfig { GpuDrivers = new GpuDriverConfig { Intel = true } });
        Assert.Single(model.GraphicsDrivers).Enabled = false;
        var request = model.CaptureSaveRequest();
        var fresh = new AppConfig
        {
            GpuDrivers = new GpuDriverConfig { Intel = true, Nvidia = true },
            PluginInstances = [new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }]
        };
        var merged = SettingsSaveMerge.Apply(fresh, request, request.Splash).Config;
        Assert.False(merged.GpuDrivers!.Intel);
        Assert.False(merged.GpuDrivers.Amd);
        Assert.True(merged.GpuDrivers.Nvidia);
        Assert.Equal("vendor.plugin", Assert.Single(merged.PluginInstances).PluginId);
        model.AdvanceSharedBaseline(request);
        Assert.Empty(model.CaptureSaveRequest().CommonPluginEdits);
    }

    [Fact]
    public void GraphicsEditMadeDuringSaveRemainsPending()
    {
        var model = Model(new AppConfig { GpuDrivers = new GpuDriverConfig() });
        var intel = Assert.Single(model.GraphicsDrivers);
        intel.Enabled = true;
        var request = model.CaptureSaveRequest();
        intel.Enabled = false;
        model.AdvanceSharedBaseline(request);
        Assert.False(Assert.Single(model.CaptureSaveRequest().CommonPluginEdits).Enabled);
    }

    private static SettingsViewModel Model(AppConfig config)
    {
        var model = SettingsTestServices.Model(config);
        model.GraphicsDrivers.Clear();
        model.GraphicsDrivers.Add(new CommonPluginInstanceRow("wsgm.gpu.intel", "default", "Intel graphics",
            config.GpuDrivers!.Intel, true));
        return model;
    }
}
