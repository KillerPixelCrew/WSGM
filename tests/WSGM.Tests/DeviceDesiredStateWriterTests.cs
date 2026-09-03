using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

public sealed class DeviceDesiredStateWriterTests
{
    private const string Machine = "claw-8a2vm";
    private const string Fan = "fan.mode";

    private static CapabilityValue Choice(string option) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = option,
    };

    private static CapabilityValue Watts(int watts) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = watts,
    };

    private static DeviceCapabilityPreference Stored(
        DeviceIntegrationConfig device,
        string capabilityId = Fan,
        string? instanceId = null) => device.Profiles
            .Single(profile => profile.DeviceIdentityKey == Machine)
            .Capabilities
            .Single(capability => capability.CapabilityId == capabilityId
                && capability.InstanceId == instanceId);

    [Fact]
    public void AValueSetOnTheDesktopBecomesTheGlobalDefault()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));

        DeviceCapabilityPreference preference = Stored(device);
        Assert.Equal("quiet", preference.GlobalDefault?.ChoiceValue);
        Assert.Empty(preference.ApplicationOverrides);
    }

    [Fact]
    public void AValueSetWhileAGameRunsBecomesThatApplicationsOverride()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "steam:42", Choice("sport"));

        DeviceCapabilityPreference preference = Stored(device);
        Assert.Null(preference.GlobalDefault);
        DeviceApplicationDesiredValue entry = Assert.Single(preference.ApplicationOverrides);
        Assert.Equal("steam:42", entry.ApplicationId);
        Assert.Equal("sport", entry.Value?.ChoiceValue);
    }

    [Fact]
    public void AnApplicationValueLeavesTheGlobalDefaultForEverythingElseAlone()
    {
        DeviceIntegrationConfig device = new();
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "steam:42", Choice("sport"));

        DeviceCapabilityPreference preference = Stored(device);
        Assert.Equal("quiet", preference.GlobalDefault?.ChoiceValue);
        Assert.Equal("sport", Assert.Single(preference.ApplicationOverrides).Value?.ChoiceValue);
    }

    [Fact]
    public void RepeatedEditsReplaceTheSameLayerRatherThanAccumulating()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "steam:42", Choice("quiet"));
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "steam:42", Choice("sport"));
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("balanced"));
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));

        Assert.Single(device.Profiles);
        DeviceCapabilityPreference preference = Stored(device);
        Assert.Equal("quiet", preference.GlobalDefault?.ChoiceValue);
        Assert.Equal("sport", Assert.Single(preference.ApplicationOverrides).Value?.ChoiceValue);
    }

    [Fact]
    public void TwoInstancesOfOneCapabilityAreStoredSeparately()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, "lighting.zone-color", "left", null, Watts(1));
        DeviceDesiredStateWriter.Store(device, Machine, "lighting.zone-color", "right", null, Watts(2));

        Assert.Equal(1, Stored(device, "lighting.zone-color", "left").GlobalDefault?.IntegerValue);
        Assert.Equal(2, Stored(device, "lighting.zone-color", "right").GlobalDefault?.IntegerValue);
    }

    [Fact]
    public void AnEmptyApplicationIdIsTreatedAsTheGlobalLayer()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "   ", Choice("quiet"));

        DeviceCapabilityPreference preference = Stored(device);
        Assert.Equal("quiet", preference.GlobalDefault?.ChoiceValue);
        Assert.Empty(preference.ApplicationOverrides);
    }

    [Fact]
    public void AnotherMachineGetsItsOwnProfileRatherThanSharingOne()
    {
        DeviceIntegrationConfig device = new();

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));
        DeviceDesiredStateWriter.Store(device, "other-handheld", Fan, null, null, Choice("sport"));

        Assert.Equal(2, device.Profiles.Count);
        Assert.Equal("quiet", Stored(device).GlobalDefault?.ChoiceValue);
    }

    [Fact]
    public void AStoredValueIsWhatTheResolverThenReturnsForThatLayer()
    {
        DeviceIntegrationConfig device = new();
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, "steam:42", Choice("sport"));
        DeviceCapabilityPreference preference = Stored(device);

        ResolvedDeviceDesiredValue inGame = DeviceDesiredStateResolver.Resolve(
            preference,
            onAcPower: true,
            hardwareProfileId: null,
            applicationId: "steam:42");
        ResolvedDeviceDesiredValue onDesktop = DeviceDesiredStateResolver.Resolve(
            preference,
            onAcPower: true,
            hardwareProfileId: null,
            applicationId: null);

        Assert.Equal("sport", inGame.Value?.ChoiceValue);
        Assert.Equal(DeviceDesiredValueSource.ApplicationOverride, inGame.Source);
        Assert.Equal("quiet", onDesktop.Value?.ChoiceValue);
        Assert.Equal(DeviceDesiredValueSource.GlobalDefault, onDesktop.Source);
    }

    [Fact]
    public void TheWriterNeverTouchesTheLayersItDoesNotOwn()
    {
        DeviceIntegrationConfig device = new();
        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("quiet"));
        DeviceCapabilityPreference preference = Stored(device);
        preference.AcPolicy = Choice("sport");
        preference.DcPolicy = Choice("eco");
        preference.HardwareProfiles.Add(new DeviceNamedDesiredValue
        {
            ProfileId = "handheld",
            Value = Choice("silent"),
        });

        DeviceDesiredStateWriter.Store(device, Machine, Fan, null, null, Choice("balanced"));

        Assert.Equal("balanced", preference.GlobalDefault?.ChoiceValue);
        Assert.Equal("sport", preference.AcPolicy?.ChoiceValue);
        Assert.Equal("eco", preference.DcPolicy?.ChoiceValue);
        Assert.Equal("silent", Assert.Single(preference.HardwareProfiles).Value?.ChoiceValue);
    }
}
