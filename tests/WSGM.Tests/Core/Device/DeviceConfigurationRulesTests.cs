using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

public sealed class DeviceConfigurationRulesTests
{
    [Fact]
    public void LegacyDevicePreferencesAreDroppedWithoutMigratingProfilesOrOemAssignments()
    {
        const string json = """
                            {"DeviceIntegration":{"OemAssignments":[{"ControlId":"old-button","Action":"ToggleWsgmOverlay"}],
                              "PluginSettings":[{"DeviceDefinitionId":"old-device","PluginId":"wsgm.device.old",
                                "Profiles":[{"ProfileId":"old-curve","CapabilityId":"thermal.fan-curve"}]}]}}
                            """;
        var config = ConfigRepair.Deserialize(json)!;

        DeviceConfigurationRules.Normalize(config.DeviceIntegration);

        Assert.Empty(config.DeviceIntegration.OemAssignments);
        Assert.Empty(config.DeviceIntegration.DeviceProfiles);
        Assert.Equal(DeviceIntegrationConfig.CurrentPreferencesSchemaVersion,
            config.DeviceIntegration.PreferencesSchemaVersion);
        Assert.DoesNotContain("PluginSettings", JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void CurrentSchemaPreservesNewAssignmentsAndBothKindsOfAuthoredProfileAfterReload()
    {
        var config = new AppConfig();
        DeviceConfigurationRules.Normalize(config.DeviceIntegration);
        config.DeviceIntegration.OemAssignments.Add(new DeviceOemAssignment
            { ControlId = "guide", Action = OemAction.ToggleSteamOverlay });
        config.DeviceIntegration.DeviceProfiles.Add(new DeviceProfileScope
        {
            DeviceDefinitionId = "ms-1t52", FamilyId = "msi-claw",
            Profiles =
            [
                new DeviceAuthoredProfile
                {
                    ProfileId = "quiet", CapabilityId = "thermal.fan-curve",
                    Curve =
                    [
                        new AuthoredCurvePoint { Input = 40, Output = 20 },
                        new AuthoredCurvePoint { Input = 80, Output = 100 }
                    ]
                },
                new DeviceAuthoredProfile { ProfileId = "amber", CapabilityId = "lighting.color", Color = 0xFF9D3D }
            ]
        });
        var reloaded = ConfigRepair.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig))!;

        DeviceConfigurationRules.Normalize(reloaded.DeviceIntegration);

        Assert.Equal(OemAction.ToggleSteamOverlay, Assert.Single(reloaded.DeviceIntegration.OemAssignments).Action);
        var profiles = Assert.Single(reloaded.DeviceIntegration.DeviceProfiles).Profiles;
        Assert.Equal(2, profiles[0].Curve.Count);
        Assert.Equal(0xFF9D3D, profiles[1].Color);
    }

    [Fact]
    public void OldSchemaDropsOnlyDeviceIntentAndKeepsGpuAndPerformancePreferences()
    {
        var config = new AppConfig();
        config.DeviceIntegration.OemAssignments.Add(new DeviceOemAssignment
            { ControlId = "old", Action = OemAction.ToggleWsgmOverlay });
        config.Profiles.Global.SustainedWatts = 30;
        config.Profiles.Global.UnifiedWatts = 25;
        config.Profiles.Global.ControllerTarget = ManagedControllerTarget.DualShock4;
        config.Profiles.Global.FanCurveProfileId = "old";
        config.Profiles.Global.FrameLimit = 60;
        config.Profiles.Global.Device =
        [
            new ProfileDeviceValue
            {
                DeviceIdentityKey = "old-device", CapabilityId = "fan.mode",
                Value = CapabilityValue.Choice("custom")
            },
            new ProfileDeviceValue
            {
                DeviceIdentityKey = "gpu:vendor", CapabilityId = "graphics.toggle",
                Value = CapabilityValue.Boolean(true)
            }
        ];
        config.SteamInputLeaseEnabled = false;

        var normalized = AppConfigRules.Normalize(config).Value;

        Assert.Null(normalized.Profiles.Global.SustainedWatts);
        Assert.Null(normalized.Profiles.Global.UnifiedWatts);
        Assert.Null(normalized.Profiles.Global.ControllerTarget);
        Assert.Null(normalized.Profiles.Global.FanCurveProfileId);
        Assert.Empty(normalized.DeviceIntegration.OemAssignments);
        Assert.Equal("gpu:vendor", Assert.Single(normalized.Profiles.Global.Device).DeviceIdentityKey);
        Assert.Equal(60, normalized.Profiles.Global.FrameLimit);
        Assert.False(normalized.SteamInputLeaseEnabled);
    }
}
