using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class PluginSettingsDeclarationCacheTests
{
    private static PluginSettingsManifest Manifest(string settingId = "vendor.flag")
    {
        return new PluginSettingsManifest
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings =
            [
                new PluginSettingDescriptor
                {
                    SettingId = settingId,
                    ValueKind = CapabilityValueKind.Boolean,
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Flag" },
                    Default = new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = false
                    },
                    SectionId = "one"
                }
            ]
        };
    }

    private static AppConfig WithScope(PluginSettingsManifest? declaration)
    {
        return new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig
            {
                PluginSettings =
                [
                    new PluginSettingsScope
                    {
                        DeviceDefinitionId = "msi.claw8",
                        PluginId = "wsgm.device.msi",
                        Declaration = declaration
                    }
                ]
            }
        };
    }

    [Fact]
    public void ACachedDeclarationSurvivesTheSourceGeneratedRoundTrip()
    {
        // Settings has to draw the page without the plugin, so the declaration has to come back off
        // disk intact rather than through a WSGM-side copy of the SDK's shapes.
        var json = JsonSerializer.Serialize(
            WithScope(Manifest()),
            ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(
            json,
            ConfigJsonContext.Default.AppConfig);

        var declaration =
            restored?.DeviceIntegration.PluginSettings[0].Declaration;

        Assert.NotNull(declaration);
        Assert.Equal("vendor.flag", declaration.Settings[0].SettingId);
        Assert.Equal("one", declaration.Sections[0].SectionId);
        Assert.Equal(CapabilityValueKind.Boolean, declaration.Settings[0].ValueKind);
    }

    [Fact]
    public void AValidCachedDeclarationIsKeptOnLoad()
    {
        var config = WithScope(Manifest());

        ConfigStore.NormalizeDeviceIntegration(config.DeviceIntegration);

        Assert.NotNull(config.DeviceIntegration.PluginSettings[0].Declaration);
    }

    [Fact]
    public void AMalformedCachedDeclarationIsDroppedRatherThanRendered()
    {
        // It would otherwise produce controls whose bounds nothing has validated, and the user would
        // be editing settings that cannot be sent anywhere.
        var broken = Manifest("not a legal identifier");
        var config = WithScope(broken);

        ConfigStore.NormalizeDeviceIntegration(config.DeviceIntegration);

        Assert.Null(config.DeviceIntegration.PluginSettings[0].Declaration);
    }

    [Fact]
    public void AScopeWithNoDeclarationIsLeftAlone()
    {
        var config = WithScope(null);

        ConfigStore.NormalizeDeviceIntegration(config.DeviceIntegration);

        Assert.Single(config.DeviceIntegration.PluginSettings);
        Assert.Null(config.DeviceIntegration.PluginSettings[0].Declaration);
    }

    [Fact]
    public void PublishingADeclarationRetiresEveryOlderPresentationCache()
    {
        var config = WithScope(Manifest("old.flag"));
        config.DeviceIntegration.PluginSettings.Add(new PluginSettingsScope
        {
            DeviceDefinitionId = "msi.claw8-new",
            PluginId = "wsgm.device.msi"
        });

        PluginSettingsCoordinator.CacheDeclaration(
            config,
            "msi.claw8-new",
            "wsgm.device.msi",
            Manifest("new.flag"));

        Assert.Null(config.DeviceIntegration.PluginSettings[0].Declaration);
        Assert.Equal(
            "new.flag",
            config.DeviceIntegration.PluginSettings[1].Declaration?.Settings[0].SettingId);
    }
}
