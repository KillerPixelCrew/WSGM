using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

/// <summary>Reads one resolved device value from profile layers, for assertions.</summary>
internal static class ProfileLayersExtensions
{
    /// <summary>Resolves one device capability value and the layer that supplies it.</summary>
    /// <param name="layers">The layers to resolve.</param>
    /// <param name="deviceIdentityKey">Stable local device identity.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <returns>The value and its source.</returns>
    internal static Resolved<CapabilityValue?> Device(this ProfileLayers layers, string deviceIdentityKey,
        string capabilityId, string? instanceId)
    {
        return layers.Reference(values => values.FindDevice(deviceIdentityKey, capabilityId, instanceId)?.Value);
    }
}
