using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsSaveMergeTests
{
    [Fact]
    public void EveryMutableScalarSettingsBindingSurvivesCaptureMergeAndReload()
    {
        var loaded = AppConfigRules.Normalize(new AppConfig
        {
            StartupApps =
            [
                new StartupAppConfig
                {
                    Path = @"C:\WSGM.Tests\startup.exe", Args = "--test", Enabled = false, Elevated = true,
                    AutoRelaunch = true
                }
            ],
            Hotkey = new HotkeyConfig { Ctrl = false, Alt = false, Shift = true, Win = true, VirtualKey = 0x41 },
            GamepadChord = new GamepadChordConfig { Enabled = true, Buttons = 1, Hold = true }
        }).Value;
        loaded.GameModeLaunch.KnownDisplays =
        [
            new KnownDisplay { Target = new DisplayTargetIdentity(@"\\?\guard", null, null, "Guard", 0, 0, 1) }
        ];
        var window = SettingsTestServices.Model(loaded);
        List<string> edited = [];
        var paths = Directory
            .EnumerateFiles(Path.Combine(RepositoryFiles.Root, "src", "WSGM", "Settings", "Pages"), "*.axaml")
            .SelectMany(file => XDocument.Load(file).Descendants())
            .Where(element => element.Name.LocalName != "TextBlock")
            .SelectMany(element => element.Attributes())
            .Where(attribute =>
                attribute.Name.LocalName is "IsChecked" or "Text" or "Value" or "SelectedIndex" or "SelectedItem")
            .Select(attribute => Regex.Match(attribute.Value, @"^\{Binding (?<path>[A-Za-z][A-Za-z0-9.]*)[,}]"))
            .Where(match => match.Success).Select(match => match.Groups["path"].Value).Distinct().ToArray();
        foreach (var path in paths)
        {
            if (path == "SelectedSuggestionIndex")
            {
                continue; // Choosing an add-program suggestion is window state, not saved policy.
            }

            object owner = window;
            PropertyInfo? property = null;
            var parts = path.Split('.');
            for (var index = 0; index < parts.Length; index++)
            {
                property = owner.GetType().GetProperty(parts[index]);
                if (property is null)
                {
                    break; // A row template uses its own view model rather than SettingsViewModel.
                }

                if (index != parts.Length - 1)
                {
                    owner = property.GetValue(owner)!;
                }
            }

            if (property?.SetMethod?.IsPublic is not true)
            {
                continue;
            }

            var current = property.GetValue(owner);
            var value = current switch
            {
                bool boolean => !boolean,
                int integer => path.EndsWith("Index", StringComparison.Ordinal) ? integer == 0 ? 1 : 0 : integer + 1,
                double number => number + 1,
                decimal number => number + 1,
                string => path.Contains("Color", StringComparison.Ordinal) ? "#123456"
                    : path == "OsdCustomOrder" ? "FPS,Time" : "edited-" + path,
                Enum enumeration => Enum.GetValues(enumeration.GetType()).Cast<object>()
                    .First(item => !item.Equals(current)),
                _ => null // Selected rows and collection editors are covered by their feature tests.
            };
            if (property.PropertyType.IsValueType || property.PropertyType == typeof(string))
            {
                Assert.NotNull(value);
            }

            if (value is not null)
            {
                property.SetValue(owner, value);
                edited.Add(path);
            }
        }

        Assert.NotEmpty(edited);
        Assert.Contains("DeviceIntegrationEnabled", edited);
        Assert.Contains("DeviceAutoTdpEnabled", edited);
        Assert.Contains("Splash.SpinnerStyle", edited);
        var request = window.CaptureSaveRequest();
        var fresh = AppConfigRules.Normalize(new AppConfig()).Value;
        var (merged, _) = SettingsSaveMerge.Apply(fresh, request, request.Splash);
        var rebound = SettingsTestServices.Model(merged);
        var reloaded = rebound.CaptureSaveRequest();

        Assert.DoesNotContain(edited, path => !Equals(ReadBinding(window, path), ReadBinding(rebound, path)));

        Assert.Equal(JsonSerializer.Serialize(request.Values, ConfigJsonContext.Default.AppConfig),
            JsonSerializer.Serialize(reloaded.Values, ConfigJsonContext.Default.AppConfig));
    }

    private static object? ReadBinding(object owner, string path)
    {
        var value = owner;
        foreach (var part in path.Split('.'))
        {
            value = value!.GetType().GetProperty(part)!.GetValue(value);
        }

        return value;
    }

    [Fact]
    public void UntouchedSharedFieldsAndMediaRecoveryStateKeepTheFreshValues()
    {
        var window = SettingsTestServices.Model(AppConfigRules.Normalize(new AppConfig()).Value);
        var request = window.CaptureSaveRequest();
        Assert.Empty(request.SharedEdits);
        var fresh = AppConfigRules.Normalize(new AppConfig()).Value;
        foreach (var field in WsgmSharedSettings.All)
        {
            var current = field.Read(fresh);
            var changed = current is bool boolean
                ? !boolean
                : Enum.GetValues(current.GetType()).Cast<object>().First(item => !item.Equals(current));
            field.Write(fresh, changed);
        }

        fresh.Themes.HiddenThemes = ["runtime-theme"];
        fresh.Themes.TranslationsBranch = ThemeTranslationBranch.Beta;
        fresh.Sounds.Selected = "runtime-sounds";
        fresh.Animations.Boot = "runtime-movie";
        fresh.Animations.ShuffleOnStart = true;
        fresh.Animations.BootVolume = 42;
        fresh.Animations.SteamSetAside = new SteamStartupMovieSetAside
            { MovieId = "steam-movie", LocalPath = "steam.webm", Shuffle = true };
        var expected = WsgmSharedSettings.All.ToDictionary(field => field.Name, field => field.Read(fresh));
        var mediaBefore = JsonSerializer.Serialize(new { fresh.Themes, fresh.Sounds, fresh.Animations });

        var (merged, _) = SettingsSaveMerge.Apply(fresh, request, request.Splash);

        Assert.Same(fresh, merged);
        Assert.All(WsgmSharedSettings.All, field => Assert.Equal(expected[field.Name], field.Read(merged)));
        Assert.Equal(mediaBefore, JsonSerializer.Serialize(new { merged.Themes, merged.Sounds, merged.Animations }));
    }

    [Fact]
    public void WorkerSnapshotPreservesRuntimeOwnedValuesThatTheWindowDidNotEdit()
    {
        var values = AppConfigRules.Normalize(new AppConfig
        {
            SteamAutoRelaunch = true,
            AccentColor = "#123456",
            StartupApps = [new StartupAppConfig { Path = "new.exe" }]
        }).Value;
        values.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        values.SteamStorageFormatEnabled = false;
        values.DeviceIntegration.AutoTdpEnabled = false;
        values.Profiles.Global.ControllerTarget = ManagedControllerTarget.Xbox360;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.NativeSteam;

        var fresh = AppConfigRules.Normalize(new AppConfig
        {
            LastSelectedPowerSchemeId = Guid.NewGuid()
        }).Value;
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
            "")
        {
            // The window changed this one itself, so its value is written over the saved one.
            SharedEdits = ["SteamStorageFormatEnabled"]
        };

        var savedPowerScheme = fresh.LastSelectedPowerSchemeId;
        var (merged, _) = SettingsSaveMerge.Apply(fresh, request, values.Splash);
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
    public void ASecondUnrelatedSaveStillKeepsTheChangeMadeInSteam()
    {
        // The window keeps showing what it loaded. Had the first save taken the merged result as its
        // baseline, the unchanged field would look edited on the second and write the stale value.
        var loaded = AppConfigRules.Normalize(new AppConfig()).Value;
        loaded.Cef.WifiIndicator = true;
        var window = SettingsTestServices.Model(loaded);
        var saved = AppConfigRules.Normalize(new AppConfig()).Value;
        saved.Cef.WifiIndicator = false;

        var first = window.CaptureSaveRequest();
        (saved, _) = SettingsSaveMerge.Apply(saved, first, first.Splash);
        window.AdvanceSharedBaseline(first);
        var second = window.CaptureSaveRequest();
        (saved, _) = SettingsSaveMerge.Apply(saved, second, second.Splash);

        Assert.Empty(second.SharedEdits);
        Assert.False(saved.Cef.WifiIndicator);
    }

    [Fact]
    public void ASettingChangedInSteamWhileTheWindowWasOpenIsNotRevertedByItsSave()
    {
        // WSGM's page in Steam writes these fields too. The window still holds what it loaded, and
        // writing that back would silently undo the change made in Steam.
        var values = AppConfigRules.Normalize(new AppConfig()).Value;
        values.Cef.WifiIndicator = true;
        values.StartMode = SessionStartMode.Game;
        values.SteamInputLeaseEnabled = true;
        var fresh = AppConfigRules.Normalize(new AppConfig()).Value;
        fresh.Cef.WifiIndicator = false;
        fresh.StartMode = SessionStartMode.Desktop;
        fresh.SteamInputLeaseEnabled = false;

        var request = new SettingsViewModel.SaveRequest(
            values, values.Splash, new Dictionary<string, CapabilityValue>(), null, "", "")
        {
            // Only the start mode was changed in the window.
            SharedEdits = ["StartMode"]
        };

        var (merged, _) = SettingsSaveMerge.Apply(fresh, request, values.Splash);

        Assert.False(merged.Cef.WifiIndicator);
        Assert.False(merged.SteamInputLeaseEnabled);
        Assert.Equal(SessionStartMode.Game, merged.StartMode);
    }

    [Fact]
    public void ADeviceEditMadeDuringSaveRemainsPendingAndTheNextSaveAcknowledgesIt()
    {
        var window = SettingsTestServices.Model(AppConfigRules.Normalize(new AppConfig()).Value);
        var original = window.DeviceAutoTdpEnabled;
        window.DeviceAutoTdpEnabled = !original;
        var first = window.CaptureSaveRequest();
        Assert.Contains("DeviceIntegration.AutoTdpEnabled", first.SharedEdits);
        window.DeviceAutoTdpEnabled = original;
        window.AdvanceSharedBaseline(first);
        var second = window.CaptureSaveRequest();
        Assert.Contains("DeviceIntegration.AutoTdpEnabled", second.SharedEdits);
        window.AdvanceSharedBaseline(second);
        Assert.DoesNotContain("DeviceIntegration.AutoTdpEnabled", window.CaptureSaveRequest().SharedEdits);
    }

    [Fact]
    public void AnAcknowledgedDeviceValueDoesNotOverwriteAFutureRuntimeChangeOnUnrelatedSave()
    {
        var window = SettingsTestServices.Model(AppConfigRules.Normalize(new AppConfig()).Value);
        window.DeviceAutoTdpEnabled = !window.DeviceAutoTdpEnabled;
        var first = window.CaptureSaveRequest();
        window.AdvanceSharedBaseline(first);
        var fresh = AppConfigRules.Normalize(new AppConfig()).Value;
        fresh.DeviceIntegration.AutoTdpEnabled = !first.Values.DeviceIntegration.AutoTdpEnabled;
        var next = window.CaptureSaveRequest();
        Assert.DoesNotContain("DeviceIntegration.AutoTdpEnabled", next.SharedEdits);
        Assert.Equal(!first.Values.DeviceIntegration.AutoTdpEnabled, SettingsSaveMerge.Apply(fresh, next, next.Splash)
            .Config.DeviceIntegration.AutoTdpEnabled);
    }

    [Fact]
    public void CommonPluginAcknowledgesTheCapturedToggleAndPreservesALaterToggle()
    {
        CommonPluginInstanceRow row = new("plugin", "instance", "Name", false, true);
        row.Enabled = true;
        var saved = row.Capture();
        row.Enabled = false;
        row.AcceptSaved(saved);
        Assert.True(row.Edited);
        row.AcceptSaved(row.Capture());
        Assert.False(row.Edited);
    }

    [Fact]
    public void WorkerSnapshotMergesOnlyEditedPluginValuesAndProfilesIntoTheFreshScope()
    {
        var values = AppConfigRules.Normalize(new AppConfig()).Value;
        values.DeviceIntegration.AutoTdpEnabled = true;
        values.Profiles.Global.ControllerTarget = ManagedControllerTarget.DualShock4;
        values.DeviceIntegration.GlyphSelection = DeviceGlyphSelection.ManualReviewedProfile;

        var fresh = AppConfigRules.Normalize(new AppConfig()).Value;
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
        fresh.Profiles.Global.FanCurveProfileId = "old";
        fresh.Profiles.Games =
            [new GameProfile { Id = "game", Values = new ProfileValues { FanCurveProfileId = "old" } }];

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
            "plugin")
        {
            SharedEdits =
            [
                "DeviceIntegration.AutoTdpEnabled", "Profiles.Global.ControllerTarget",
                "DeviceIntegration.GlyphSelection"
            ]
        };

        var (merged, _) = SettingsSaveMerge.Apply(fresh, request, values.Splash);

        var scope = Assert.Single(merged.DeviceIntegration.PluginSettings);
        Assert.Equal("keep", scope.Values.Single(value => value.SettingId == "runtime-only").Text);
        var edited = scope.Values.Single(value => value.SettingId == "edited");
        Assert.Equal(0xAABBCC, edited.Color);
        Assert.Null(edited.Integer);
        Assert.Equal("new", Assert.Single(scope.Profiles).ProfileId);
        Assert.Null(merged.Profiles.Global.FanCurveProfileId);
        Assert.Null(Assert.Single(merged.Profiles.Games).Values.FanCurveProfileId);
        Assert.True(merged.DeviceIntegration.AutoTdpEnabled);
        Assert.Equal(ManagedControllerTarget.DualShock4, merged.Profiles.Global.ControllerTarget);
        Assert.Equal(
            DeviceGlyphSelection.ManualReviewedProfile,
            merged.DeviceIntegration.GlyphSelection);
    }
}
