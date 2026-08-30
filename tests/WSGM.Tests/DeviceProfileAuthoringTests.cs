using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;

namespace WSGM.Tests;

public sealed class DeviceProfileAuthoringTests
{
    private const string Device = "msi.claw8";
    private const string Plugin = "wsgm.device.msi";

    private static AppConfig Config(params DeviceAuthoredProfile[] profiles) => new()
    {
        DeviceIntegration = new DeviceIntegrationConfig
        {
            PluginSettings =
            [
                new PluginSettingsScope
                {
                    DeviceDefinitionId = Device,
                    PluginId = Plugin,
                    Declaration = new PluginSettingsManifest(),
                    Profiles = [.. profiles],
                },
            ],
        },
    };

    private static DeviceAuthoredProfile Stored(string id, string name) => new()
    {
        ProfileId = id,
        Name = name,
        CapabilityId = "thermal.fan-curve",
        Curve =
        [
            new AuthoredCurvePoint { Input = 0, Output = 10 },
            new AuthoredCurvePoint { Input = 100, Output = 90 },
        ],
    };

    [Fact]
    public void StoredProfilesLoadIntoTheEditor()
    {
        SettingsViewModel viewModel = new(Config(Stored("quiet", "Quiet")));

        DeviceProfileRowViewModel row = Assert.Single(viewModel.DeviceProfiles);
        Assert.Equal("Quiet", row.Name);
        Assert.Equal(2, row.Curve.Count);
        Assert.Same(row, viewModel.SelectedDeviceProfile);
    }

    [Fact]
    public void AddingAProfileSeedsACurveTheUserCanGrab()
    {
        // A curve needs two points to be valid, and an editor opening on an empty plot gives the
        // user nothing to drag.
        SettingsViewModel viewModel = new(Config());

        viewModel.AddDeviceProfile("thermal.fan-curve");

        DeviceProfileRowViewModel row = Assert.Single(viewModel.DeviceProfiles);
        Assert.Equal(2, row.Curve.Count);
        Assert.Same(row, viewModel.SelectedDeviceProfile);
    }

    [Fact]
    public void RemovingTheSelectedProfileSelectsAnother()
    {
        SettingsViewModel viewModel = new(Config(Stored("a", "A"), Stored("b", "B")));
        viewModel.SelectedDeviceProfile = viewModel.DeviceProfiles[0];

        viewModel.RemoveSelectedDeviceProfile();

        Assert.Single(viewModel.DeviceProfiles);
        Assert.Equal("b", viewModel.SelectedDeviceProfile?.ProfileId);
    }

    [Fact]
    public void RemovingTheLastProfileLeavesNothingSelected()
    {
        SettingsViewModel viewModel = new(Config(Stored("a", "A")));

        viewModel.RemoveSelectedDeviceProfile();

        Assert.Empty(viewModel.DeviceProfiles);
        Assert.False(viewModel.HasSelectedDeviceProfile);
    }

    [Fact]
    public void ARenameKeepsTheProfileIdSoOverridesAreNotOrphaned()
    {
        SettingsViewModel viewModel = new(Config(Stored("quiet", "Quiet")));
        viewModel.DeviceProfiles[0].Name = "Silent";

        DeviceAuthoredProfile stored = viewModel.DeviceProfiles[0].ToStored();

        Assert.Equal("quiet", stored.ProfileId);
        Assert.Equal("Silent", stored.Name);
    }

    [Fact]
    public void AnEditedProfileListIsWrittenAtSave()
    {
        SettingsViewModel viewModel = new(Config(Stored("a", "A")));
        viewModel.AddDeviceProfile("thermal.fan-curve");

        AppConfig fresh = Config(Stored("a", "A"));
        viewModel.ApplyDeviceProfilesTo(fresh);

        Assert.Equal(2, fresh.DeviceIntegration.PluginSettings[0].Profiles.Count);
    }

    [Fact]
    public void AnUntouchedProfileListIsLeftAsAnotherProcessWroteIt()
    {
        // A save triggered by an unrelated page must not overwrite what something else put there.
        SettingsViewModel viewModel = new(Config(Stored("a", "A")));

        AppConfig fresh = Config(Stored("a", "A"), Stored("b", "B"));
        viewModel.ApplyDeviceProfilesTo(fresh);

        Assert.Equal(2, fresh.DeviceIntegration.PluginSettings[0].Profiles.Count);
    }

    [Fact]
    public void AnEmptyNameFallsBackToTheIdRatherThanPersistingBlank()
    {
        SettingsViewModel viewModel = new(Config(Stored("quiet", "Quiet")));
        viewModel.DeviceProfiles[0].Name = "   ";

        Assert.Equal("quiet", viewModel.DeviceProfiles[0].ToStored().Name);
    }

    [Fact]
    public void AProfileNameIsBoundedToWhatStorageAccepts()
    {
        SettingsViewModel viewModel = new(Config(Stored("quiet", "Quiet")));
        viewModel.DeviceProfiles[0].Name = new string('x', 200);

        Assert.Equal(
            DeviceAuthoredProfile.MaxNameLength,
            viewModel.DeviceProfiles[0].Name.Length);
    }

    [Fact]
    public void AnAuthoredCurveRoundTripsThroughTheStoredShape()
    {
        SettingsViewModel viewModel = new(Config(Stored("quiet", "Quiet")));
        viewModel.DeviceProfiles[0].Curve = [new CurvePoint(20, 30), new CurvePoint(80, 70)];

        DeviceAuthoredProfile stored = viewModel.DeviceProfiles[0].ToStored();

        Assert.Equal([20, 80], stored.Curve.Select(point => point.Input));
        Assert.Equal([30, 70], stored.Curve.Select(point => point.Output));
    }
}
