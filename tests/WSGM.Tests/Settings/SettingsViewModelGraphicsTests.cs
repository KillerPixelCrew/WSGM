using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsViewModelGraphicsTests
{
    [Fact]
    public void BuiltinGraphicsChoicesAreAvailableBeforePackageDiscoveryWithOtherIntegrationsOff()
    {
        var config = new AppConfig();
        config.DeviceIntegration.Enabled = false;
        config.Cef.Enabled = false;
        var model = SettingsTestServices.Model(config);

        Assert.Equal(["Intel graphics", "AMD graphics", "NVIDIA graphics"],
            model.GraphicsDrivers.Select(row => row.Name));
        Assert.All(model.GraphicsDrivers, row => Assert.True(row.Enabled));
        Assert.Empty(model.CaptureSaveRequest().CommonPluginEdits);
    }

    [Fact]
    public async Task PackageRefreshPreservesGraphicsDraftsAndHidesRetiredPackageInstances()
    {
        var config = new AppConfig
        {
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.intel", InstanceId = "custom", Enabled = false },
                new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }
            ]
        };
        var model = SettingsTestServices.Model(config);
        var intel = model.GraphicsDrivers.Single(row => row.Name == "Intel graphics (custom)");
        Assert.False(intel.Enabled);
        intel.Enabled = true;

        await model.LoadPluginPackagesAsync();
        await model.LoadPluginPackagesAsync();

        Assert.Same(intel, model.GraphicsDrivers.Single(row => row.Name == "Intel graphics (custom)"));
        Assert.True(intel.Enabled);
        Assert.Equal("vendor.plugin / default (package unavailable)", Assert.Single(model.CommonPlugins).Detail);
        var edit = Assert.Single(model.CaptureSaveRequest().CommonPluginEdits);
        Assert.Equal(("wsgm.gpu.intel", "custom", true), (edit.PluginId, edit.InstanceId, edit.Enabled));
    }

    [Fact]
    public void GraphicsSaveMergesOnlyTheEditedInstanceAndKeepsFreshSteamChanges()
    {
        var loaded = new AppConfig();
        var model = SettingsTestServices.Model(loaded);
        var intel = model.GraphicsDrivers.Single(row => row.Name == "Intel graphics");
        intel.Enabled = false;
        var request = model.CaptureSaveRequest();
        var fresh = new AppConfig
        {
            PluginInstances =
            [
                new CommonPluginInstanceConfig { PluginId = "wsgm.gpu.amd", Enabled = false },
                new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }
            ]
        };

        var merged = SettingsSaveMerge.Apply(fresh, request, request.Splash).Config;

        Assert.False(merged.PluginInstances.Single(instance => instance.PluginId == "wsgm.gpu.intel").Enabled);
        Assert.False(merged.PluginInstances.Single(instance => instance.PluginId == "wsgm.gpu.amd").Enabled);
        Assert.True(merged.PluginInstances.Single(instance => instance.PluginId == "vendor.plugin").Enabled);
        Assert.DoesNotContain(merged.PluginInstances, instance => instance.PluginId == "wsgm.gpu.nvidia");
        model.AdvanceSharedBaseline(request);
        Assert.Empty(model.CaptureSaveRequest().CommonPluginEdits);
    }

    [Fact]
    public void GraphicsEditMadeDuringSaveRemainsPendingUntilTheNextSave()
    {
        var model = SettingsTestServices.Model(new AppConfig());
        var intel = model.GraphicsDrivers.Single(row => row.Name == "Intel graphics");
        intel.Enabled = false;
        var request = model.CaptureSaveRequest();
        intel.Enabled = true;

        model.AdvanceSharedBaseline(request);

        Assert.True(Assert.Single(model.CaptureSaveRequest().CommonPluginEdits).Enabled);
        model.AdvanceSharedBaseline(model.CaptureSaveRequest());
        Assert.Empty(model.CaptureSaveRequest().CommonPluginEdits);
    }
}
