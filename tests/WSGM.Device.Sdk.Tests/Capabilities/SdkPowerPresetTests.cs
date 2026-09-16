using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using static WSGM.Device.Tests.PowerLimitDescriptors;

namespace WSGM.Device.Tests;

public sealed class SdkPowerPresetTests
{
    private static CapabilityDescriptor[] Pair(params DevicePowerPreset[] presets) =>
        [Limit(CapabilityRole.PowerSustainedLimit) with { PowerPresets = presets }, Limit(CapabilityRole.PowerSlowLimit)];

    [Fact]
    public void EmptyPresetsPreserveExistingDescriptors()
    {
        var sustained = Limit(CapabilityRole.PowerSustainedLimit);
        Assert.NotNull(sustained.PowerPresets);
        Assert.Empty(sustained.PowerPresets);
        Assert.True(DevicePowerPreset.TryValidate([sustained, Limit(CapabilityRole.PowerSlowLimit)], out _));
    }

    [Fact]
    public void AssignmentSnapshotsMutableArraysAndLists()
    {
        var preset = new DevicePowerPreset("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery);
        DevicePowerPreset[] array = [preset];
        List<DevicePowerPreset> list = [preset];
        var fromArray = Limit(CapabilityRole.PowerSustainedLimit) with { PowerPresets = array };
        var fromList = Limit(CapabilityRole.PowerSustainedLimit) with { PowerPresets = list };
        array[0] = preset with { SustainedWatts = 37 };
        list.Clear();
        Assert.Equal(preset, Assert.Single(fromArray.PowerPresets));
        Assert.Equal(preset, Assert.Single(fromList.PowerPresets));
        var exposed = Assert.IsAssignableFrom<IList<DevicePowerPreset>>(fromArray.PowerPresets);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed[0] = array[0]);
    }

    [Fact]
    public void ValidTargetsRoundTripWithTheDescriptorSet()
    {
        var preset = new DevicePowerPreset("battery", "Super Battery", 8, 9, DevicePowerMode.BetterBattery);
        CapabilityDescriptorSet set = new() { Generation = 1, CycleGeneration = 1, Descriptors = Pair(preset) };
        Assert.True(DevicePowerPreset.TryValidate(set.Descriptors, out _));
        var json = JsonSerializer.Serialize(set);
        var read = JsonSerializer.Deserialize<CapabilityDescriptorSet>(json)!;
        Assert.Equal(preset, Assert.Single(read.Descriptors[0].PowerPresets));
    }

    [Theory]
    [InlineData("custom", "Valid", 8, 9, 0)]
    [InlineData("bad id", "Valid", 8, 9, 0)]
    [InlineData("valid", "bad\nlabel", 8, 9, 0)]
    [InlineData("valid", "Valid", 7, 9, 0)]
    [InlineData("valid", "Valid", 9, 8, 0)]
    [InlineData("valid", "Valid", 8, 38, 0)]
    [InlineData("valid", "Valid", 8, 9, 99)]
    public void InvalidPresetsAreRejected(string id, string name, int sustained, int slow, int mode) =>
        Assert.False(DevicePowerPreset.TryValidate(Pair(new DevicePowerPreset(id, name, sustained, slow, (DevicePowerMode)mode)), out _));

    [Fact]
    public void MissingAmbiguousReadonlyOrSteppedPartnersAreRejected()
    {
        var pair = Pair(new DevicePowerPreset("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery));
        Assert.False(DevicePowerPreset.TryValidate([pair[0]], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. pair, pair[1]], out _));
        Assert.False(DevicePowerPreset.TryValidate([pair[0], pair[1] with { SupportsWrite = false }], out _));
        Assert.False(DevicePowerPreset.TryValidate([pair[0], pair[1] with { Step = 2 }], out _));
        Assert.False(DevicePowerPreset.TryValidate([pair[0] with { InstanceId = "second" }, pair[1]], out _));
    }

    [Fact]
    public void DuplicateAndExcessivePresetsAreRejected()
    {
        var preset = new DevicePowerPreset("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery);
        Assert.False(DevicePowerPreset.TryValidate(Pair(preset, preset), out _));
        Assert.False(DevicePowerPreset.TryValidate(Pair(Enumerable.Range(0, 17).Select(i => preset with { Id = $"p{i}" }).ToArray()), out _));
    }

    [Fact]
    public void ScenarioTargetsRequireBothSourcesAndOneWritableChoiceCapability()
    {
        var preset = new DevicePowerPreset("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery)
        { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" };
        var scenario = new CapabilityDescriptor
        {
            CapabilityId = "scenario",
            Role = CapabilityRole.ScenarioMode,
            ValueKind = CapabilityValueKind.Choice,
            Persistence = CapabilityPersistence.Volatile,
            Display = new CapabilityDisplay { Key = DisplayKey.PerformanceProfile },
            SupportsRead = true,
            SupportsWrite = true,
            Choices = [new CapabilityChoice("eco", new CapabilityDisplay { Key = DisplayKey.PerformanceProfile }),
                new CapabilityChoice("comfort", new CapabilityDisplay { Key = DisplayKey.PerformanceProfile })]
        };
        Assert.True(DevicePowerPreset.TryValidate([.. Pair(preset), scenario], out _));
        Assert.Equal(preset, JsonSerializer.Deserialize<DevicePowerPreset>(JsonSerializer.Serialize(preset)));
        Assert.False(DevicePowerPreset.TryValidate(Pair(preset), out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), scenario, scenario], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), scenario with { SupportsWrite = false }], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), scenario with { InstanceId = "second" }], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), scenario with { AvailableOnDc = false }], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), scenario with { AvailableOnAc = false }], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset with { ScenarioOnDc = null }), scenario], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset with { ScenarioOnAc = "missing" }), scenario], out _));
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset with { ScenarioOnDc = "missing" }), scenario], out _));
        var json = JsonSerializer.Serialize(scenario with { Choices = null! });
        var malformed = JsonSerializer.Deserialize<CapabilityDescriptor>(json)!;
        Assert.Null(malformed.Choices);
        Assert.False(DevicePowerPreset.TryValidate([.. Pair(preset), malformed], out _));
    }
}
