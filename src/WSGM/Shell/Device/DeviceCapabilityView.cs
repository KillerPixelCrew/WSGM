using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

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
