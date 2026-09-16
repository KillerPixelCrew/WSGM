using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Settings;

public sealed class PluginSettingsViewModelTests
{
    private static SettingsViewModel ViewModel() => new(new AppConfig());

    private static PluginSettingDescriptor Setting(string id, string? sectionId) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id },
        Default = new CapabilityValue { Kind = CapabilityValueKind.Boolean },
        SectionId = sectionId
    };

    /// <remarks>
    /// Through the one shared projection, not a Settings-specific copy: the overlay draws from the
    /// same call, so a second arrangement here would let the two surfaces disagree about where a
    /// plugin's settings live.
    /// </remarks>
    private static PluginSettingsView Page(PluginSettingsManifest manifest, params string[] ids) =>
        PluginSettingsCoordinator.Project(
            manifest,
            new PluginSettingsResolution(
                [.. ids.Select(id => new EffectivePluginSetting(
                    id,
                    new CapabilityValue { Kind = CapabilityValueKind.Boolean },
                    PluginSettingOrigin.Default,
                    null))],
                []));

    [Fact]
    public void APageWithNoSectionsReportsItselfUnavailableRatherThanDrawingNothing()
    {
        var viewModel = ViewModel();

        viewModel.SetPluginSettings(Page(new PluginSettingsManifest()), (_, _) => { });

        Assert.False(viewModel.PluginSettingsAvailable);
        Assert.NotEmpty(viewModel.PluginSettingsEmptyReason);
    }

    [Fact]
    public void SectionsAndRowsArriveInRenderOrder()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections =
            [
                new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.Power },
                new PluginSettingSection
                {
                    SectionId = "two",
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Vendor",
                    SortOrder = 1
                }
            ],
            Settings = [Setting("a", "one"), Setting("b", "two")]
        };

        var viewModel = ViewModel();
        viewModel.SetPluginSettings(Page(manifest, "a", "b"), (_, _) => { });

        Assert.True(viewModel.PluginSettingsAvailable);
        Assert.Equal(
            ["one", "two"],
            viewModel.PluginSettingSections.Select(section => section.SectionId));
        Assert.Equal("POWER", viewModel.PluginSettingSections[0].Title);
        Assert.Equal("VENDOR", viewModel.PluginSettingSections[1].Title);
    }

    [Fact]
    public void EditingARowReachesTheOwnerWithItsSettingId()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("vendor.flag", "one")]
        };

        var viewModel = ViewModel();
        List<string> edited = [];
        viewModel.SetPluginSettings(Page(manifest, "vendor.flag"), (id, _) => edited.Add(id));

        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        Assert.Equal("vendor.flag", Assert.Single(edited));
    }

    [Fact]
    public void RebuildingReplacesTheRowsRatherThanAccumulatingThem()
    {
        // The manifest changes only when a plugin is installed or updated, so a wholesale rebuild is
        // the correct path -- but it must not leave the previous plugin's rows behind.
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("a", "one")]
        };

        var viewModel = ViewModel();
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });

        Assert.Single(viewModel.PluginSettingSections);
        Assert.Single(viewModel.PluginSettingSections[0].Rows);
    }

    [Fact]
    public void AnOldRowStopsReachingTheOwnerAfterARebuild()
    {
        // Each rebuild subscribes fresh handlers. A retained row from the previous build would
        // otherwise keep writing settings for a manifest that is no longer installed.
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("a", "one")]
        };

        var viewModel = ViewModel();
        var firstOwnerEdits = 0;
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => firstOwnerEdits++);
        var stale = viewModel.PluginSettingSections[0].Rows[0];

        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });
        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        Assert.Equal(0, firstOwnerEdits);
        Assert.NotSame(stale, viewModel.PluginSettingSections[0].Rows[0]);
    }

    private const string Device = "msi.claw8";

    private const string Plugin = "wsgm.device.msi";

    private static PluginSettingsManifest Manifest(string label = "Flag") => new()
    {
        Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
        Settings =
        [
            new PluginSettingDescriptor
            {
                SettingId = "vendor.flag",
                ValueKind = CapabilityValueKind.Boolean,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
                Default = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Boolean,
                    BooleanValue = false
                },
                SectionId = "one"
            }
        ]
    };

    private static AppConfig Config(PluginSettingsManifest? declaration) => new()
    {
        DeviceIntegration = new DeviceIntegrationConfig
        {
            PluginSettings =
            [
                new PluginSettingsScope
                {
                    DeviceDefinitionId = Device,
                    PluginId = Plugin,
                    Declaration = declaration
                }
            ]
        }
    };

    [Fact]
    public void ACachedDeclarationProducesAnEditablePage()
    {
        SettingsViewModel viewModel = new(Config(Manifest()));

        Assert.True(viewModel.PluginSettingsAvailable);
        Assert.Equal(
            "vendor.flag",
            viewModel.PluginSettingSections[0].Rows[0].SettingId);
    }

    [Fact]
    public void NoCachedDeclarationSaysSoRatherThanShowingABlankPage()
    {
        SettingsViewModel viewModel = new(Config(null));

        Assert.False(viewModel.PluginSettingsAvailable);
        Assert.Contains("plugin", viewModel.PluginSettingsEmptyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEditReachesTheConfigurationTheSaveActuallyWrites()
    {
        // The save re-reads configuration from disk and applies the view model onto THAT object, so
        // an edit written to the loaded copy would be silently discarded.
        SettingsViewModel viewModel = new(Config(Manifest()));
        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        var fresh = Config(Manifest());
        viewModel.ApplyPluginSettingsTo(fresh);

        var stored = Assert.Single(fresh.DeviceIntegration.PluginSettings[0].Values);
        Assert.Equal("vendor.flag", stored.SettingId);
        Assert.True(stored.Boolean);
    }

    [Fact]
    public void AnUntouchedSettingIsLeftExactlyAsAnotherProcessWroteIt()
    {
        // The running shell owns the same store while Settings is open, so writing an unedited
        // snapshot over the fresh load would silently revert it.
        SettingsViewModel viewModel = new(Config(Manifest()));

        var fresh = Config(Manifest());
        fresh.DeviceIntegration.PluginSettings[0].Values.Add(new PluginSettingValue
        {
            SettingId = "vendor.flag",
            Boolean = true
        });
        viewModel.ApplyPluginSettingsTo(fresh);

        Assert.True(fresh.DeviceIntegration.PluginSettings[0].Values[0].Boolean);
    }

    [Fact]
    public void AStoredValueTheDeclarationNoLongerAllowsFallsBackToItsDefault()
    {
        // A cache written by an older plugin build can describe bounds the stored values no longer
        // fit, and the page must not offer a value the plugin would refuse.
        var config = Config(Manifest());
        config.DeviceIntegration.PluginSettings[0].Values.Add(new PluginSettingValue
        {
            SettingId = "vendor.flag",
            // Wrong shape for a boolean setting: the integer field is set and the boolean is not.
            Integer = 7
        });

        SettingsViewModel viewModel = new(config);

        Assert.False(viewModel.PluginSettingSections[0].Rows[0].BooleanValue);
    }

    [Fact]
    public void TheMostRecentlyPublishedDeclarationWinsOverAStaleScope()
    {
        var config = Config(Manifest("Stale"));
        PluginSettingsScope current = new()
        {
            DeviceDefinitionId = "msi.claw8-current",
            PluginId = Plugin,
            Declaration = Manifest("Current")
        };
        config.DeviceIntegration.PluginSettings.Add(current);

        SettingsViewModel viewModel = new(config);

        Assert.Equal("Current", viewModel.PluginSettingSections[0].Rows[0].Label);
    }

    [Fact]
    public void SettingsSelectsOnlyTheCurrentlyInstalledPluginDeclaration()
    {
        var config = Config(Manifest("Replaced"));
        config.DeviceIntegration.PluginSettings.Add(new PluginSettingsScope
        {
            DeviceDefinitionId = "other.device",
            PluginId = "wsgm.device.current",
            Declaration = Manifest("Installed")
        });

        SettingsViewModel viewModel = new(config, "wsgm.device.current");

        Assert.Equal("Installed", viewModel.PluginSettingSections[0].Rows[0].Label);
    }

    [Fact]
    public void EmptyPluginSlotDoesNotExposeAReplacedPluginDeclaration()
    {
        SettingsViewModel viewModel = new(Config(Manifest("Replaced")), installedPluginId: null);

        Assert.False(viewModel.PluginSettingsAvailable);
    }
}
