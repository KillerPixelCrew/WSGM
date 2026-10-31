using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Reads, clears and copies profile layers.</summary>
public static class ProfileFields
{
    /// <summary>The managed-controller target used when no layer sets one.</summary>
    public const ManagedControllerTarget DefaultControllerTarget = ManagedControllerTarget.SteamDeckComposite;

    private static readonly ProfileField[] Fields = Enum.GetValues<ProfileField>();

    /// <summary>The fields Steam's Performance tab reset clears from Global.</summary>
    public static readonly ProfileField[] PerformanceTab =
    [
        ProfileField.FrameLimit, ProfileField.OverlayLevel, ProfileField.TdpUnified, ProfileField.UnifiedWatts,
        ProfileField.SustainedWatts, ProfileField.VariableRefreshRate,
        ProfileField.CpuBoost, ProfileField.AutoTdpTarget
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
            ProfileField.AutoTdpTarget => values.AutoTdpTargetFps is not null,
            ProfileField.AcPowerPreset => values.AcPowerPreset is not null,
            ProfileField.BatteryPowerPreset => values.BatteryPowerPreset is not null,
            ProfileField.FanCurveProfile => values.FanCurveProfileId is not null,
            ProfileField.LightingProfile => values.LightingProfileId is not null,
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
            case ProfileField.AutoTdpTarget:
                values.AutoTdpTargetFps = null;
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
            case ProfileField.LightingProfile:
                values.LightingProfileId = null;
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

    /// <summary>Stores an integer field's value in a layer.</summary>
    /// <param name="values">The layer.</param>
    /// <param name="field">An integer field.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentOutOfRangeException">The field is not an integer profile field.</exception>
    public static void Set(this ProfileValues values, ProfileField field, int value)
    {
        ArgumentNullException.ThrowIfNull(values);
        switch (field)
        {
            case ProfileField.FrameLimit:
                values.FrameLimit = value;
                break;
            case ProfileField.OverlayLevel:
                values.OverlayLevel = value;
                break;
            case ProfileField.UnifiedWatts:
                values.UnifiedWatts = value;
                break;
            case ProfileField.SustainedWatts:
                values.SustainedWatts = value;
                break;
            case ProfileField.AutoTdpTarget:
                values.AutoTdpTargetFps = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Not an integer profile field.");
        }
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
