using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

public sealed class SettingsSaveMergeTests
{
    [Fact]
    public void WorkerSnapshotPreservesRuntimeOwnedValuesThatTheWindowDidNotEdit()
    {
        var values = ConfigStore.Normalize(new AppConfig
        {
            SteamAutoRelaunch = true,
            AccentColor = "#123456",
            StartupApps = [new StartupAppConfig { Path = "new.exe" }]
        });
        values.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        values.SteamStorageFormatEnabled = false;
        values.DeviceIntegration.AutoTdpEnabled = false;
        values.Profiles.Global.ControllerTarget = ManagedControllerTarget.Xbox360;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.NativeSteam;

        var fresh = ConfigStore.Normalize(new AppConfig
        {
            LastSelectedPowerSchemeId = Guid.NewGuid()
        });
        // Written by the running shell while the window was open: the entry it is still inside
        // owes the desktop this layout, and a save must not drop it.
        fresh.GameModeLaunchRecovery.PendingReturnLayout = new DisplayLayout([
            new DisplayLayoutOutput(new DisplayTargetIdentity(@"\\?\a", null, null, "A", 0, 0, 1), 0, 0, 1920, 1080,
                DisplayRefresh.FromHertz(60))
        ]);
        fresh.GameModeLaunchRecovery.PendingReturnAudio = new AudioProfilePreference
        {
            Output = new AudioEndpointPreference { Id = "runtime-output", Name = "Runtime speakers" }
        };
        fresh.LibraryTabOrder = ["fresh-tab"];
        fresh.QuickAccessPins = ["fresh-pin"];
        fresh.PreviousShellSnapshotCaptured = true;
        fresh.PreviousShellValue = "explorer.exe";
        fresh.DeviceIntegration.AutoTdpEnabled = true;
        fresh.Profiles.Global.ControllerTarget = ManagedControllerTarget.DualShock4;
        fresh.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.ManualReviewedProfile;

        var request = new SettingsViewModel.SaveRequest(
            values,
            values.Splash,
            new Dictionary<string, CapabilityValue>(),
            null,
            "",
            "",
            false,
            false,
            false,
            false)
        {
            // The window changed this one itself, so its value is written over the saved one.
            SharedEdits = ["SteamStorageFormatEnabled"]
        };

        var savedPowerScheme = fresh.LastSelectedPowerSchemeId;
        var merged = SettingsViewModel.ApplyCapturedValues(fresh, request, values.Splash);
        Assert.Equal(savedPowerScheme, merged.LastSelectedPowerSchemeId);

        Assert.True(merged.SteamAutoRelaunch);
        Assert.Equal("#123456", merged.AccentColor);
        Assert.Equal("new.exe", Assert.Single(merged.StartupApps).Path);
        Assert.Equal(GameModeLaunchKind.Custom, merged.GameModeLaunch.Kind);
        Assert.NotNull(merged.GameModeLaunchRecovery.PendingReturnLayout);
        Assert.Equal("runtime-output", merged.GameModeLaunchRecovery.PendingReturnAudio!.Output!.Id);
        Assert.False(merged.SteamStorageFormatEnabled);
        Assert.Equal("fresh-tab", Assert.Single(merged.LibraryTabOrder));
        Assert.Equal("fresh-pin", Assert.Single(merged.QuickAccessPins));
        Assert.True(merged.PreviousShellSnapshotCaptured);
        Assert.Equal("explorer.exe", merged.PreviousShellValue);
        Assert.True(merged.DeviceIntegration.AutoTdpEnabled);
        Assert.Equal(ManagedControllerTarget.DualShock4, merged.Profiles.Global.ControllerTarget);
        Assert.Equal(
            DeviceGlyphSelection.ManualReviewedProfile,
            merged.DeviceIntegration.GlyphSelection);
    }

    [Fact]
    public void ASettingChangedInSteamWhileTheWindowWasOpenIsNotRevertedByItsSave()
    {
        // WSGM's page in Steam writes these fields too. The window still holds what it loaded, and
        // writing that back would silently undo the change made in Steam.
        var values = ConfigStore.Normalize(new AppConfig());
        values.Cef.WifiIndicator = true;
        values.StartMode = SessionStartMode.Game;
        values.SteamInputLeaseEnabled = true;
        var fresh = ConfigStore.Normalize(new AppConfig());
        fresh.Cef.WifiIndicator = false;
        fresh.StartMode = SessionStartMode.Desktop;
        fresh.SteamInputLeaseEnabled = false;

        var request = new SettingsViewModel.SaveRequest(
            values, values.Splash, new Dictionary<string, CapabilityValue>(), null, "", "", false, false, false,
            false)
        {
            // Only the start mode was changed in the window.
            SharedEdits = ["StartMode"]
        };

        var merged = SettingsViewModel.ApplyCapturedValues(fresh, request, values.Splash);

        Assert.False(merged.Cef.WifiIndicator);
        Assert.False(merged.SteamInputLeaseEnabled);
        Assert.Equal(SessionStartMode.Game, merged.StartMode);
    }

    [Fact]
    public void WorkerSnapshotMergesOnlyEditedPluginValuesAndProfilesIntoTheFreshScope()
    {
        var values = ConfigStore.Normalize(new AppConfig());
        values.DeviceIntegration.AutoTdpEnabled = true;
        values.Profiles.Global.ControllerTarget = ManagedControllerTarget.DualShock4;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.ManualReviewedProfile;

        var fresh = ConfigStore.Normalize(new AppConfig());
        fresh.DeviceIntegration.PluginSettings.Add(new PluginSettingsScope
        {
            DeviceDefinitionId = "device",
            PluginId = "plugin",
            Values =
            [
                new PluginSettingValue { SettingId = "edited", Integer = 1 },
                new PluginSettingValue { SettingId = "runtime-only", Text = "keep" }
            ],
            Profiles = [new DeviceAuthoredProfile { ProfileId = "old", Name = "Old" }]
        });

        var edits = new Dictionary<string, CapabilityValue>
        {
            ["edited"] = new()
            {
                Kind = CapabilityValueKind.Color,
                ColorValue = 0xAABBCC
            }
        };
        DeviceAuthoredProfile[] profiles =
        [
            new() { ProfileId = "new", Name = "New" }
        ];
        var request = new SettingsViewModel.SaveRequest(
            values,
            values.Splash,
            edits,
            profiles,
            "device",
            "plugin",
            true,
            true,
            true,
            false);

        var merged = SettingsViewModel.ApplyCapturedValues(fresh, request, values.Splash);

        var scope = Assert.Single(merged.DeviceIntegration.PluginSettings);
        Assert.Equal("keep", scope.Values.Single(value => value.SettingId == "runtime-only").Text);
        var edited = scope.Values.Single(value => value.SettingId == "edited");
        Assert.Equal(0xAABBCC, edited.Color);
        Assert.Null(edited.Integer);
        Assert.Equal("new", Assert.Single(scope.Profiles).ProfileId);
        Assert.True(merged.DeviceIntegration.AutoTdpEnabled);
        Assert.Equal(ManagedControllerTarget.DualShock4, merged.Profiles.Global.ControllerTarget);
        Assert.Equal(
            DeviceGlyphSelection.ManualReviewedProfile,
            merged.DeviceIntegration.GlyphSelection);
    }
}
