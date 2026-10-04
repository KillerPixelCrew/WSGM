using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Every setting a profile layer can hold.</summary>
public enum ProfileField
{
    /// <summary>RTSS frame limit.</summary>
    FrameLimit,

    /// <summary>Performance-overlay level.</summary>
    OverlayLevel,

    /// <summary>Manual power mode.</summary>
    TdpUnified,

    /// <summary>Unified power target.</summary>
    UnifiedWatts,

    /// <summary>Sustained power limit.</summary>
    SustainedWatts,

    /// <summary>Variable refresh.</summary>
    VariableRefreshRate = 6,

    /// <summary>Windows' processor boost mode.</summary>
    CpuBoost,

    /// <summary>AC power preset.</summary>
    AcPowerPreset,

    /// <summary>Battery power preset.</summary>
    BatteryPowerPreset,

    /// <summary>Authored fan-curve profile.</summary>
    FanCurveProfile,

    /// <summary>Managed-controller target.</summary>
    ControllerTarget,

    /// <summary>One device capability value, named by capability and instance.</summary>
    Device
}

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

/// <summary>Reads, clears and copies profile layers.</summary>
public static class ProfileFields
{
    private static readonly ProfileField[] Fields = Enum.GetValues<ProfileField>();

    /// <summary>The managed-controller target used when no layer sets one.</summary>
    public const ManagedControllerTarget DefaultControllerTarget = ManagedControllerTarget.SteamDeckComposite;

    /// <summary>The fields Steam's Performance tab reset clears from Global.</summary>
    public static readonly ProfileField[] PerformanceTab =
    [
        ProfileField.FrameLimit, ProfileField.OverlayLevel, ProfileField.TdpUnified, ProfileField.UnifiedWatts,
        ProfileField.SustainedWatts, ProfileField.VariableRefreshRate,
        ProfileField.CpuBoost
    ];

    /// <summary>Whether a layer sets a value.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">
    ///     The device, for a device setting. The key's own publisher wins over it; with neither, any device
    ///     package value matches, never a graphics package's.
    /// </param>
    /// <returns>True when the layer holds a value for it.</returns>
    public static bool Has(this ProfileValues values, ProfileSettingKey key, string? deviceIdentityKey = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return key.Field switch
        {
            ProfileField.FrameLimit => values.FrameLimit is not null,
            ProfileField.OverlayLevel => values.OverlayLevel is not null,
            ProfileField.TdpUnified => values.TdpUnified is not null,
            ProfileField.UnifiedWatts => values.UnifiedWatts is not null,
            ProfileField.SustainedWatts => values.SustainedWatts is not null,
            ProfileField.VariableRefreshRate => values.VariableRefreshRate is not null,
            ProfileField.CpuBoost => values.CpuBoost is not null,
            ProfileField.AcPowerPreset => values.AcPowerPreset is not null,
            ProfileField.BatteryPowerPreset => values.BatteryPowerPreset is not null,
            ProfileField.FanCurveProfile => values.FanCurveProfileId is not null,
            ProfileField.ControllerTarget => values.ControllerTarget is not null,
            ProfileField.Device => values.Device.Any(entry => entry.Value is not null
                                                              && Matches(entry, key.Publisher ?? deviceIdentityKey,
                                                                  key.CapabilityId, key.InstanceId)),
            _ => false
        };
    }

    /// <summary>Removes a value from a layer, so it falls back to the layer below.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">
    ///     The device, for a device setting. The key's own publisher wins over it; with neither, it is
    ///     cleared for every device package but never for a graphics package.
    /// </param>
    /// <returns>Whether anything was removed.</returns>
    public static bool Clear(this ProfileValues values, ProfileSettingKey key, string? deviceIdentityKey = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.Has(key, deviceIdentityKey))
        {
            return false;
        }

        switch (key.Field)
        {
            case ProfileField.FrameLimit:
                values.FrameLimit = null;
                break;
            case ProfileField.OverlayLevel:
                values.OverlayLevel = null;
                break;
            case ProfileField.TdpUnified:
                values.TdpUnified = null;
                break;
            case ProfileField.UnifiedWatts:
                values.UnifiedWatts = null;
                break;
            case ProfileField.SustainedWatts:
                values.SustainedWatts = null;
                break;
            case ProfileField.VariableRefreshRate:
                values.VariableRefreshRate = null;
                break;
            case ProfileField.CpuBoost:
                values.CpuBoost = null;
                break;
            case ProfileField.AcPowerPreset:
                values.AcPowerPreset = null;
                break;
            case ProfileField.BatteryPowerPreset:
                values.BatteryPowerPreset = null;
                break;
            case ProfileField.FanCurveProfile:
                values.FanCurveProfileId = null;
                break;
            case ProfileField.ControllerTarget:
                values.ControllerTarget = null;
                break;
            case ProfileField.Device:
                var publisher = key.Publisher ?? deviceIdentityKey;
                values.Device.RemoveAll(entry =>
                    Matches(entry, publisher, key.CapabilityId, key.InstanceId));
                break;
        }

        return true;
    }

    /// <summary>Finds one stored device value.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="deviceIdentityKey">The device.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <returns>The stored entry, or null.</returns>
    public static ProfileDeviceValue? FindDevice(this ProfileValues values, string deviceIdentityKey,
        string? capabilityId, string? instanceId)
    {
        return values.Device.FirstOrDefault(entry => Matches(entry, deviceIdentityKey, capabilityId, instanceId));
    }

    /// <summary>Stores one device value in a layer.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="deviceIdentityKey">The device.</param>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <param name="value">The value.</param>
    public static void SetDevice(this ProfileValues values, string deviceIdentityKey, string capabilityId,
        string? instanceId, CapabilityValue value)
    {
        var entry = values.FindDevice(deviceIdentityKey, capabilityId, instanceId);
        if (entry is null)
        {
            entry = new ProfileDeviceValue
            {
                DeviceIdentityKey = deviceIdentityKey,
                CapabilityId = capabilityId,
                InstanceId = string.IsNullOrEmpty(instanceId) ? null : instanceId
            };
            values.Device.Add(entry);
        }

        entry.Value = value;
    }

    /// <summary>The number of settings a layer holds.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="livePublishers">
    ///     The identity keys of the device and graphics packages running now, so a value stored for a
    ///     package that is gone is not counted. Null counts every stored value.
    /// </param>
    /// <returns>How many values it sets.</returns>
    public static int Count(this ProfileValues values, IReadOnlyCollection<string>? livePublishers = null)
    {
        return Fields.Count(field => field is not ProfileField.Device
                                                             && values.Has(new ProfileSettingKey(field)))
               + values.Device.Count(entry => entry.Value is not null
                                              && (livePublishers is null
                                                  || livePublishers.Contains(entry.DeviceIdentityKey)));
    }

    private static bool Matches(ProfileDeviceValue entry, string? deviceIdentityKey, string? capabilityId,
        string? instanceId)
    {
        return (deviceIdentityKey is null
                   ? !ProfileSettingKey.IsGpuPublisher(entry.DeviceIdentityKey)
                   : string.Equals(entry.DeviceIdentityKey, deviceIdentityKey, StringComparison.Ordinal))
               && string.Equals(entry.CapabilityId, capabilityId, StringComparison.Ordinal)
               && string.Equals(entry.InstanceId ?? string.Empty, instanceId ?? string.Empty,
                   StringComparison.Ordinal);
    }
}
