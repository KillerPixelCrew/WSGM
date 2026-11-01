using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>One stored device capability value.</summary>
public sealed class ProfileDeviceValue
{
    /// <summary>Stable local device identity.</summary>
    public string DeviceIdentityKey { get; set; } = string.Empty;

    /// <summary>Semantic capability identifier.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Descriptor instance, or null for a single-instance capability.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The value.</summary>
    public CapabilityValue? Value { get; set; }
}
