using System;
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

    /// <summary>Boost power limit.</summary>
    BoostWatts,

    /// <summary>Variable refresh.</summary>
    VariableRefreshRate,

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
public readonly record struct ProfileSettingKey(
    ProfileField Field,
    string? CapabilityId = null,
    string? InstanceId = null)
{
    private const string DevicePrefix = "device:";

    /// <summary>The stable string form Steam surfaces carry back to WSGM.</summary>
    public string Id => Field is ProfileField.Device
        ? $"{DevicePrefix}{CapabilityId}#{InstanceId}"
        : Field.ToString();

    /// <summary>Names one device capability value.</summary>
    /// <param name="capabilityId">The capability.</param>
    /// <param name="instanceId">Its instance, or null.</param>
    /// <returns>The key.</returns>
    public static ProfileSettingKey ForDevice(string capabilityId, string? instanceId)
    {
        return new ProfileSettingKey(ProfileField.Device, capabilityId,
            string.IsNullOrEmpty(instanceId) ? null : instanceId);
    }

    /// <summary>Parses <see cref="Id" />.</summary>
    /// <param name="id">The string form.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns>Whether <paramref name="id" /> names a setting.</returns>
    public static bool TryParse(string? id, out ProfileSettingKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
        {
            return false;
        }

        if (id.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            var body = id[DevicePrefix.Length..];
            var split = body.IndexOf('#');
            if (split <= 0)
            {
                return false;
            }

            key = ForDevice(body[..split], body[(split + 1)..]);
            return true;
        }

        if (!Enum.TryParse<ProfileField>(id, false, out var field) || field is ProfileField.Device
                                                                   || !Enum.IsDefined(field))
        {
            return false;
        }

        key = new ProfileSettingKey(field);
        return true;
    }
}

/// <summary>Reads, clears and copies profile layers.</summary>
public static class ProfileFields
{
    /// <summary>The managed-controller target used when no layer sets one.</summary>
    public const ManagedControllerTarget DefaultControllerTarget = ManagedControllerTarget.SteamDeckComposite;

    /// <summary>The fields Steam's Performance tab reset clears from Global.</summary>
    public static readonly ProfileField[] PerformanceTab =
    [
        ProfileField.FrameLimit, ProfileField.OverlayLevel, ProfileField.TdpUnified, ProfileField.UnifiedWatts,
        ProfileField.SustainedWatts, ProfileField.BoostWatts, ProfileField.VariableRefreshRate,
        ProfileField.CpuBoost
    ];

    /// <summary>Whether a layer sets a value.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">The device, for a device setting; null matches any device.</param>
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
            ProfileField.BoostWatts => values.BoostWatts is not null,
            ProfileField.VariableRefreshRate => values.VariableRefreshRate is not null,
            ProfileField.CpuBoost => values.CpuBoost is not null,
            ProfileField.AcPowerPreset => values.AcPowerPreset is not null,
            ProfileField.BatteryPowerPreset => values.BatteryPowerPreset is not null,
            ProfileField.FanCurveProfile => values.FanCurveProfileId is not null,
            ProfileField.ControllerTarget => values.ControllerTarget is not null,
            ProfileField.Device => values.Device.Any(entry => entry.Value is not null
                                                              && Matches(entry, deviceIdentityKey, key.CapabilityId,
                                                                  key.InstanceId)),
            _ => false
        };
    }

    /// <summary>Removes a value from a layer, so it falls back to the layer below.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="key">The setting.</param>
    /// <param name="deviceIdentityKey">The device, for a device setting; null clears it for every device.</param>
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
            case ProfileField.BoostWatts:
                values.BoostWatts = null;
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
                values.Device.RemoveAll(entry =>
                    Matches(entry, deviceIdentityKey, key.CapabilityId, key.InstanceId));
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
    /// <returns>How many values it sets.</returns>
    public static int Count(this ProfileValues values)
    {
        return Enum.GetValues<ProfileField>().Count(field => field is not ProfileField.Device
                                                             && values.Has(new ProfileSettingKey(field)))
               + values.Device.Count(entry => entry.Value is not null);
    }

    /// <summary>A detached deep copy of the store.</summary>
    /// <param name="config">The store.</param>
    /// <returns>A copy sharing no mutable state.</returns>
    public static ProfileConfig Copy(this ProfileConfig config)
    {
        return JsonSerializer.Deserialize(
                   JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.ProfileConfig),
                   ConfigJsonContext.Default.ProfileConfig)
               ?? new ProfileConfig();
    }

    private static bool Matches(ProfileDeviceValue entry, string? deviceIdentityKey, string? capabilityId,
        string? instanceId)
    {
        return (deviceIdentityKey is null
                || string.Equals(entry.DeviceIdentityKey, deviceIdentityKey, StringComparison.Ordinal))
               && string.Equals(entry.CapabilityId, capabilityId, StringComparison.Ordinal)
               && string.Equals(entry.InstanceId ?? string.Empty, instanceId ?? string.Empty,
                   StringComparison.Ordinal);
    }
}
