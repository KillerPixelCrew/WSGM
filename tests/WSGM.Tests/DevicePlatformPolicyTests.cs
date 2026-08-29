using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DevicePlatformPolicyTests
{
    [Fact]
    public void OldConfigurationDefaultsToNoDeviceHost()
    {
        AppConfig config = ConfigStore.Normalize(new AppConfig { DeviceIntegration = null! });

        Assert.False(config.DeviceIntegration.Enabled);
        Assert.Equal(ManagedControllerTarget.SteamDeckComposite,
            config.DeviceIntegration.ControllerTarget);
    }

    [Fact]
    public void DisablingTheMasterDoesNotEraseTheControllerPreference()
    {
        AppConfig config = ConfigStore.Normalize(new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig
            {
                Enabled = false,
                ControllerManagementEnabled = true,
            },
        });

        Assert.False(config.DeviceIntegration.Enabled);
        Assert.True(config.DeviceIntegration.ControllerManagementEnabled);
    }

    [Fact]
    public void DesiredStateUsesTheFrozenLayerPrecedence()
    {
        DeviceCapabilityPreference preference = new()
        {
            CapabilityId = "power.primary-limit",
            GlobalDefault = Value(10),
            AcPolicy = Value(12),
            HardwareProfiles = [new DeviceNamedDesiredValue { ProfileId = "balanced", Value = Value(15) }],
            ApplicationOverrides = [new DeviceApplicationDesiredValue { ApplicationId = "game", Value = Value(18) }],
        };

        Assert.Equal(20, DeviceDesiredStateResolver.Resolve(
            preference, true, "balanced", "game", Value(20)).Value?.IntegerValue);
        Assert.Equal(18, DeviceDesiredStateResolver.Resolve(
            preference, true, "balanced", "game", null).Value?.IntegerValue);
        Assert.Equal(15, DeviceDesiredStateResolver.Resolve(
            preference, true, "balanced", null, null).Value?.IntegerValue);
        Assert.Equal(12, DeviceDesiredStateResolver.Resolve(
            preference, true, null, null, null).Value?.IntegerValue);
        Assert.Equal(10, DeviceDesiredStateResolver.Resolve(
            preference, false, null, null, null).Value?.IntegerValue);
    }

    [Fact]
    public void DescriptorValidationRejectsDuplicateAndStaleShapes()
    {
        CapabilityDescriptor descriptor = Descriptor();
        CapabilityDescriptorSet duplicated = new()
        {
            Generation = 2,
            CycleGeneration = 3,
            Descriptors = [descriptor, descriptor],
        };

        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated, 3, 1, out _));
        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated with { Descriptors = [descriptor], Generation = 1 }, 3, 1, out _));
    }

    private static CapabilityValue Value(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private static CapabilityDescriptor Descriptor() => new()
    {
        CapabilityId = "power.primary-limit",
        Role = CapabilityRole.PowerSustainedLimit,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.Tdp },
        SupportsRead = true,
        SupportsWrite = true,
        Minimum = 8,
        Maximum = 30,
        Step = 1,
        Persistence = CapabilityPersistence.Volatile,
    };

}
