using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Tests;

/// <summary>A readable, writable 8-37 W limit for power pair and preset validation tests.</summary>
internal static class PowerLimitDescriptors
{
    internal static CapabilityDescriptor Limit(CapabilityRole role, string? id = null) => new()
    {
        CapabilityId = id ?? role.ToString(),
        Role = role,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
        Persistence = CapabilityPersistence.Volatile,
        SupportsRead = true,
        SupportsWrite = true,
        Unit = CapabilityUnit.Watt,
        Minimum = 8,
        Maximum = 37,
        Step = 1
    };
}
