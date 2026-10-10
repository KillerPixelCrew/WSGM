using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceLightingProfileTests
{
    private const string Identity = "fixture:machine";
    private static readonly CapabilityDescriptor[] Zones = [Zone("left"), Zone("right")];

    [Fact]
    public void GlobalNamedColorYieldsToAPerGameManualZoneWithoutSavingDerivedValues()
    {
        var global = new ProfileValues { LightingProfileId = "blue" };
        var game = new ProfileValues();
        game.SetDevice(Identity, "native.color", "right", CapabilityValue.Color(0xFF0000));
        var projected = DeviceCoordinator.LightingProfileDesiredLayers(new ProfileLayers(global, game),
            [Color("blue", 0x0000FF)], Zones, Identity);

        Assert.Equal(0x0000FF, projected.Device(Identity, "native.color", "left").Value?.ColorValue);
        Assert.Equal(ProfileSource.Global, projected.Device(Identity, "native.color", "left").Source);
        Assert.Equal(0xFF0000, projected.Device(Identity, "native.color", "right").Value?.ColorValue);
        Assert.Equal(ProfileSource.Game, projected.Device(Identity, "native.color", "right").Source);
        Assert.Empty(global.Device);
        Assert.Single(game.Device);
    }

    [Fact]
    public void PerGameNamedProfileSuppliesEveryZoneAndClearRestoresInheritance()
    {
        var global = new ProfileValues { LightingProfileId = "blue" };
        var game = new ProfileValues { LightingProfileId = "green" };
        DeviceAuthoredProfile[] profiles = [Color("blue", 0x0000FF), Color("green", 0x00FF00)];
        var projected =
            DeviceCoordinator.LightingProfileDesiredLayers(new ProfileLayers(global, game), profiles, Zones, Identity);
        Assert.All(Zones, zone => Assert.Equal(0x00FF00,
            projected.Device(Identity, zone.CapabilityId, zone.InstanceId).Value?.ColorValue));
        Assert.True(game.Clear(new ProfileSettingKey(ProfileField.LightingProfile)));
        projected = DeviceCoordinator.LightingProfileDesiredLayers(new ProfileLayers(global, game), profiles, Zones,
            Identity);
        Assert.All(Zones, zone => Assert.Equal(0x0000FF,
            projected.Device(Identity, zone.CapabilityId, zone.InstanceId).Value?.ColorValue));
        Assert.Empty(game.Device);
    }

    [Fact]
    public void MissingColorDoesNotInventBlackOrOverwriteManualIntent()
    {
        var global = new ProfileValues { LightingProfileId = "missing-color" };
        global.SetDevice(Identity, "native.color", "left", CapabilityValue.Color(0x123456));
        var projected = DeviceCoordinator.LightingProfileDesiredLayers(new ProfileLayers(global, null),
            [Color("missing-color", null)], Zones, Identity);
        Assert.Equal(0x123456, projected.Device(Identity, "native.color", "left").Value?.ColorValue);
        Assert.Null(projected.Device(Identity, "native.color", "right").Value);
    }

    [Fact]
    public void DeletedLightingProfileReferencesAreClearedInEveryLayer()
    {
        var profiles = new ProfileConfig
        {
            Global = new ProfileValues { LightingProfileId = "deleted" },
            Games = [new GameProfile { Id = "steam:42", Values = new ProfileValues { LightingProfileId = "deleted" } }]
        };
        Assert.True(ProfileEdits.RemoveLightingProfileReferences(profiles, "deleted"));
        Assert.Null(profiles.Global.LightingProfileId);
        Assert.Null(profiles.Games[0].Values.LightingProfileId);
        Assert.False(ProfileEdits.RemoveLightingProfileReferences(profiles, "deleted"));
    }

    [Fact]
    public void SharedSelectorKeepsPartialFailureVisibleAndAllowsExplicitRetry()
    {
        var row = DeviceOverlayBridge.LightingProfileView([Color("blue", 0x0000FF)],
            new Resolved<string?>("blue", ProfileSource.Game),
            "Applied to 1 of 2 lighting zones. The write is uncertain.",
            DescriptorStatus.Warning);
        Assert.Equal(DeviceHostRowIds.LightingProfile, row.Id);
        Assert.Equal(nameof(ProfileField.LightingProfile), row.OverrideId);
        Assert.Equal(DescriptorStatus.Warning, row.Status);
        Assert.Contains("1 of 2", row.Description);
        Assert.True(row.CanInvoke);
    }

    private static DeviceAuthoredProfile Color(string id, int? color)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = id, Name = id, CapabilityId = CapabilityIds.LightingColor, Color = color
        };
    }

    private static CapabilityDescriptor Zone(string instance)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "native.color", InstanceId = instance, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
            Display = new CapabilityDisplay { Key = DisplayKey.Lighting }
        };
    }
}
