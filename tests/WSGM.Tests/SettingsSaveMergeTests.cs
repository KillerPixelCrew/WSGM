using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Settings;

namespace WSGM.Tests;

public sealed class SettingsSaveMergeTests
{
    [Fact]
    public void WorkerSnapshotPreservesRuntimeOwnedValuesThatTheWindowDidNotEdit()
    {
        AppConfig values = ConfigStore.Normalize(new AppConfig
        {
            SteamAutoRelaunch = true,
            AccentColor = "#123456",
            StartupApps = [new StartupAppConfig { Path = "new.exe" }],
        });
        values.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        values.DeviceIntegration.AutoTdpEnabled = false;
        values.DeviceIntegration.ControllerTarget = ManagedControllerTarget.Xbox360;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.NativeSteam;

        AppConfig fresh = ConfigStore.Normalize(new AppConfig
        {
            LastSelectedPowerSchemeId = Guid.NewGuid(),
        });
        // Written by the running shell while the window was open: the entry it is still inside
        // owes the desktop this layout, and a save must not drop it.
        fresh.GameModeLaunchRecovery.PendingReturnLayout = new([
            new(new(@"\\?\a", null, null, "A", 0, 0, 1), 0, 0, 1920, 1080,
                WindowsDeviceControl.DisplayRefresh.FromHertz(60))]);
        fresh.DeviceIntegration.AutoTdpEnabled = true;
        fresh.DeviceIntegration.ControllerTarget = ManagedControllerTarget.DualShock4;
        fresh.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.ManualReviewedProfile;

        var request = new SettingsViewModel.SaveRequest(
            values,
            values.Splash,
            new Dictionary<string, CapabilityValue>(),
            DeviceProfiles: null,
            PluginDevice: "",
            PluginId: "",
            AutoTdpEdited: false,
            ControllerTargetEdited: false,
            GlyphSelectionEdited: false,
            QuickSetupWasAnswered: false);

        Guid? savedPowerScheme = fresh.LastSelectedPowerSchemeId;
        SettingsViewModel.ApplyCapturedValues(fresh, request, values.Splash);
        Assert.Equal(savedPowerScheme, fresh.LastSelectedPowerSchemeId);

        Assert.True(fresh.SteamAutoRelaunch);
        Assert.Equal("#123456", fresh.AccentColor);
        Assert.Equal("new.exe", Assert.Single(fresh.StartupApps).Path);
        Assert.Equal(GameModeLaunchKind.Custom, fresh.GameModeLaunch.Kind);
        Assert.NotNull(fresh.GameModeLaunchRecovery.PendingReturnLayout);
        Assert.True(fresh.DeviceIntegration.AutoTdpEnabled);
        Assert.Equal(ManagedControllerTarget.DualShock4, fresh.DeviceIntegration.ControllerTarget);
        Assert.Equal(
            DeviceGlyphSelection.ManualReviewedProfile,
            fresh.DeviceIntegration.GlyphSelection);
    }

    [Fact]
    public void WorkerSnapshotMergesOnlyEditedPluginValuesAndProfilesIntoTheFreshScope()
    {
        AppConfig values = ConfigStore.Normalize(new AppConfig());
        values.DeviceIntegration.AutoTdpEnabled = true;
        values.DeviceIntegration.ControllerTarget = ManagedControllerTarget.DualShock4;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.ManualReviewedProfile;

        AppConfig fresh = ConfigStore.Normalize(new AppConfig());
        fresh.DeviceIntegration.PluginSettings.Add(new PluginSettingsScope
        {
            DeviceDefinitionId = "device",
            PluginId = "plugin",
            Values =
            [
                new PluginSettingValue { SettingId = "edited", Integer = 1 },
                new PluginSettingValue { SettingId = "runtime-only", Text = "keep" },
            ],
            Profiles = [new DeviceAuthoredProfile { ProfileId = "old", Name = "Old" }],
        });

        var edits = new Dictionary<string, CapabilityValue>
        {
            ["edited"] = new CapabilityValue
            {
                Kind = CapabilityValueKind.Color,
                ColorValue = 0xAABBCC,
            },
        };
        DeviceAuthoredProfile[] profiles =
        [
            new DeviceAuthoredProfile { ProfileId = "new", Name = "New" },
        ];
        var request = new SettingsViewModel.SaveRequest(
            values,
            values.Splash,
            edits,
            profiles,
            PluginDevice: "device",
            PluginId: "plugin",
            AutoTdpEdited: true,
            ControllerTargetEdited: true,
            GlyphSelectionEdited: true,
            QuickSetupWasAnswered: false);

        SettingsViewModel.ApplyCapturedValues(fresh, request, values.Splash);

        PluginSettingsScope scope = Assert.Single(fresh.DeviceIntegration.PluginSettings);
        Assert.Equal("keep", scope.Values.Single(value => value.SettingId == "runtime-only").Text);
        PluginSettingValue edited = scope.Values.Single(value => value.SettingId == "edited");
        Assert.Equal(0xAABBCC, edited.Color);
        Assert.Null(edited.Integer);
        Assert.Equal("new", Assert.Single(scope.Profiles).ProfileId);
        Assert.True(fresh.DeviceIntegration.AutoTdpEnabled);
        Assert.Equal(ManagedControllerTarget.DualShock4, fresh.DeviceIntegration.ControllerTarget);
        Assert.Equal(
            DeviceGlyphSelection.ManualReviewedProfile,
            fresh.DeviceIntegration.GlyphSelection);
    }
}
