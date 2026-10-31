using System;

namespace WSGM.Core;

/// <summary>Names one setting, including a device capability instance.</summary>
/// <param name="Field">The setting.</param>
/// <param name="CapabilityId">The capability, for <see cref="ProfileField.Device" />.</param>
/// <param name="InstanceId">The capability instance, when it has one.</param>
/// <param name="Publisher">
///     The capability publisher's profile key, such as <c>gpu:wsgm.gpu.intel</c>, or null for the device
///     package.
/// </param>
public readonly record struct ProfileSettingKey(
    ProfileField Field,
    string? CapabilityId = null,
    string? InstanceId = null,
    string? Publisher = null)
{
    /// <summary>Prefix of a graphics package's profile key; the plugin id follows it.</summary>
    public const string GpuPublisherPrefix = "gpu:";

    private const string DevicePrefix = "device:";

    /// <summary>The stable string form Steam surfaces carry back to WSGM.</summary>
    /// <remarks>
    ///     A device package value keeps its short form, <c>device:capability#instance</c>. A graphics
    ///     package's value names its publisher first, <c>gpu:plugin/capability#instance</c>, because two
    ///     vendors may publish the same capability id.
    /// </remarks>
    public string Id => Field is not ProfileField.Device ? Field.ToString()
        : Publisher is { Length: > 0 } publisher ? $"{publisher}/{CapabilityId}#{InstanceId}"
        : $"{DevicePrefix}{CapabilityId}#{InstanceId}";

    /// <summary>The profile key values of a graphics package are stored under.</summary>
    /// <param name="pluginId">The package id.</param>
    /// <returns><c>gpu:</c> followed by the id.</returns>
    public static string GpuPublisher(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        return GpuPublisherPrefix + pluginId;
    }

    /// <summary>Whether a stored identity key belongs to a graphics package rather than the device.</summary>
    /// <param name="identityKey">The stored key.</param>
    /// <returns>True for a <c>gpu:</c> key.</returns>
    public static bool IsGpuPublisher(string? identityKey)
    {
        return identityKey?.StartsWith(GpuPublisherPrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>Names one device capability value.</summary>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <param name="publisher">The publisher's profile key, or null for the device package.</param>
    /// <returns>The key.</returns>
    public static ProfileSettingKey ForDevice(string capabilityId, string? instanceId, string? publisher = null)
    {
        return new ProfileSettingKey(ProfileField.Device, capabilityId,
            string.IsNullOrEmpty(instanceId) ? null : instanceId,
            string.IsNullOrEmpty(publisher) ? null : publisher);
    }

    /// <summary>Parses <see cref="Id" />.</summary>
    /// <param name="id">The string form.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns>Whether <paramref name="id" /> names a setting.</returns>
    public static bool TryParse(string? id, out ProfileSettingKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (id.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return TryParseCapability(id[DevicePrefix.Length..], null, out key);
        }

        if (id.StartsWith(GpuPublisherPrefix, StringComparison.Ordinal))
        {
            var slash = id.IndexOf('/', GpuPublisherPrefix.Length);
            return slash > GpuPublisherPrefix.Length
                   && TryParseCapability(id[(slash + 1)..], id[..slash], out key);
        }

        if (!Enum.TryParse<ProfileField>(id, false, out var field) || field is ProfileField.Device
                                                                   || !Enum.IsDefined(field))
        {
            return false;
        }

        key = new ProfileSettingKey(field);
        return true;
    }

    private static bool TryParseCapability(string body, string? publisher, out ProfileSettingKey key)
    {
        key = default;
        var split = body.IndexOf('#');
        if (split <= 0)
        {
            return false;
        }

        key = ForDevice(body[..split], body[(split + 1)..], publisher);
        return true;
    }
}
