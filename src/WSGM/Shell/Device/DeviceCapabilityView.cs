using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Stable dictionary key for one semantic capability instance.</summary>
/// <param name="CapabilityId">Semantic capability identifier, unique together with the instance identifier.</param>
/// <param name="InstanceId">Optional subdevice/zone identifier; null denotes the uninstanced capability.</param>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    /// <summary>Formats the key for diagnostics, separating a nonempty instance with a hash.</summary>
    /// <returns>The capability identifier alone, or capability and instance identifiers joined by <c>#</c>.</returns>
    public override string ToString()
    {
        return InstanceId is { Length: > 0 }
            ? $"{CapabilityId}#{InstanceId}"
            : CapabilityId;
    }
}

/// <summary>One immutable router snapshot suitable for an overlay or diagnostics client.</summary>
/// <param name="Descriptor">The capability.</param>
/// <param name="Projection">Its state, desired value and command progress.</param>
/// <param name="LastResult">How the last command finished.</param>
/// <param name="LastCommandValue">The value that command wrote, or null for an action or none.</param>
internal sealed record DeviceCapabilityView(
    CapabilityDescriptor Descriptor,
    CapabilityProjection Projection,
    CapabilityCommandResult? LastResult,
    CapabilityValue? LastCommandValue = null)
{
    /// <summary>
    ///     The publisher's profile key, such as <c>gpu:wsgm.gpu.intel</c>, or null for the device package.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>The setting id this capability's stored value is addressed by.</summary>
    public ProfileSettingKey SettingKey =>
        ProfileSettingKey.ForDevice(Descriptor.CapabilityId, Descriptor.InstanceId, Publisher);
}
