using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

public sealed class DevicePowerUserPresetsTests
{
    private static readonly DevicePowerCustomValues Values = new()
        { SustainedWatts = 12, SlowWatts = 15, WindowsMode = DevicePowerMode.Balanced };

    private static DevicePowerUserPreset Preset(string id, string name, string plugin = "claw")
    {
        return new DevicePowerUserPreset { PluginId = plugin, Id = id, Name = name, Values = Values };
    }

    [Theory]
    [InlineData("", "Enter a name for the preset.")]
    [InlineData("   ", "Enter a name for the preset.")]
    [InlineData("Two\nlines", "The preset name must be a single line of text.")]
    [InlineData(" quiet ", "A preset named “quiet” already exists.")]
    public void NamesMustBeNonEmptySingleLineAndUniquePerDeviceIgnoringCase(string name, string error)
    {
        List<DevicePowerUserPreset> saved = [Preset("user.a", "Quiet")];
        Assert.Null(DevicePowerUserPresets.ValidateName(saved, "claw", name, null, out var refusal));
        Assert.Equal(error, refusal);
    }

    [Fact]
    public void ANameIsTrimmedNeverTruncatedAndFreeOnAnotherDeviceOrForItsOwnPreset()
    {
        List<DevicePowerUserPreset> saved = [Preset("user.a", "Quiet"), Preset("user.b", "Loud", "ally")];
        var longName = new string('x', 500);
        Assert.Equal(longName, DevicePowerUserPresets.ValidateName(saved, "claw", $"  {longName} ", null, out _));
        Assert.Equal("Loud", DevicePowerUserPresets.ValidateName(saved, "claw", "Loud", null, out _));
        Assert.Equal("quiet", DevicePowerUserPresets.ValidateName(saved, "claw", "quiet", "user.a", out _));
    }

    [Fact]
    public void TheDefaultNameIsTheFirstUnusedNumberForThatDevice()
    {
        List<DevicePowerUserPreset> saved =
        [
            Preset("user.a", "Custom preset 1"), Preset("user.b", "custom preset 2"),
            Preset("user.c", "Custom preset 3", "ally")
        ];
        Assert.Equal("Custom preset 3", DevicePowerUserPresets.DefaultName(saved, "claw"));
        Assert.Equal("Custom preset 1", DevicePowerUserPresets.DefaultName([], "claw"));
    }

    [Fact]
    public void SaveRenameUpdateAndDeleteChangeOnlyTheNamedPresetOfThatDevice()
    {
        List<DevicePowerUserPreset> saved = [Preset("user.a", "Quiet"), Preset("user.a2", "Quiet", "ally")];
        var id = DevicePowerUserPresets.NewId();
        Assert.True(DevicePowerUserPresets.IsUserId(id));
        DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Save, "claw", id, " Travel ", Values));
        Assert.Equal("Travel", saved[^1].Name);

        DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Rename, "claw", id, "Train"));
        Assert.Equal("Train", saved[^1].Name);

        var louder = Values with { SustainedWatts = 20, SlowWatts = 25 };
        DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Update, "claw", id, Values: louder));
        Assert.Equal(louder, saved[^1].Values);
        Assert.Equal(id, saved[^1].Id);

        var before = DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Delete, "claw", id));
        Assert.Equal("Train", before.Name);
        Assert.Equal(["user.a", "user.a2"], saved.Select(preset => preset.Id));
    }

    [Fact]
    public void RefusedEditsChangeNothing()
    {
        List<DevicePowerUserPreset> saved = [Preset("user.a", "Quiet"), Preset("user.b", "Loud")];
        Assert.Throws<InvalidOperationException>(() => DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Rename, "claw", "user.b", "QUIET")));
        Assert.Throws<InvalidOperationException>(() => DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Save, "claw", "user.c", "New",
                Values with { SlowWatts = 5 })));
        Assert.Throws<InvalidOperationException>(() => DevicePowerUserPresets.Apply(saved,
            new DevicePowerUserPresetEdit(DevicePowerUserPresetEditKind.Delete, "ally", "user.a")));
        Assert.Equal(["Quiet", "Loud"], saved.Select(preset => preset.Name));
    }

    [Fact]
    public void DeletingTurnsEveryAssignmentOfThePresetIntoCustomHoldingItsValues()
    {
        var deleted = Preset("user.a", "Quiet");
        ProfileConfig profiles = new()
        {
            Global = new ProfileValues
            {
                AcPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "user.a" },
                BatteryPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "balanced" }
            },
            Games =
            [
                new GameProfile
                {
                    Id = "steam:1",
                    Values = new ProfileValues
                    {
                        BatteryPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "user.a" },
                        AcPowerPreset = new DevicePowerPresetReference { PluginId = "ally", PresetId = "user.a" }
                    }
                }
            ]
        };

        Assert.True(DevicePowerUserPresets.ReplaceReferences(profiles, deleted));
        Assert.Equal("custom", profiles.Global.AcPowerPreset!.PresetId);
        Assert.Equal(Values, profiles.Global.AcPowerPreset.CustomValues);
        Assert.Equal("balanced", profiles.Global.BatteryPowerPreset!.PresetId);
        Assert.Equal("custom", profiles.Games[0].Values.BatteryPowerPreset!.PresetId);
        Assert.Equal("user.a", profiles.Games[0].Values.AcPowerPreset!.PresetId);
        Assert.False(DevicePowerUserPresets.ReplaceReferences(profiles, deleted));
    }

    [Fact]
    public void NormalizationDropsIncompleteForeignAndDuplicateEntries()
    {
        DeviceIntegrationConfig device = new()
        {
            PowerPresets =
            [
                Preset(" user.a ", " Quiet "),
                Preset("user.a", "Again"),
                Preset("user.b", "QUIET"),
                Preset("user.c", "Quiet", "ally"),
                Preset("balanced", "Declared id"),
                Preset("user.d", "  "),
                new DevicePowerUserPreset { PluginId = "claw", Id = "user.e", Name = "No values" },
                new DevicePowerUserPreset
                    { PluginId = "claw", Id = "user.f", Name = "Bad", Values = Values with { SlowWatts = 1 } }
            ]
        };
        DeviceConfigurationRules.Normalize(device);
        Assert.Equal(["user.a", "user.c"], device.PowerPresets.Select(preset => preset.Id));
        Assert.Equal("Quiet", device.PowerPresets[0].Name);
    }
}
