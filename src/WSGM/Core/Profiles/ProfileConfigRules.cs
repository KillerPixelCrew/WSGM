using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Normalizes the stored profiles configuration in place before consumers use it.</summary>
internal static class ProfileConfigRules
{
    /// <summary>Brings the profile store into a shape the resolver can rely on.</summary>
    /// <param name="profiles">The store to normalize in place.</param>
    /// <param name="device">The device section, whose authored profiles a fan-curve reference must name.</param>
    /// <remarks>
    ///     Internal so its rules can be tested directly. A reference to an authored profile that no
    ///     longer exists is dropped, so that layer falls back to the one below it instead of naming
    ///     nothing.
    /// </remarks>
    /// <returns>An empty diagnostic list; these repairs do not produce warning entries.</returns>
    internal static IReadOnlyList<string> Normalize(ProfileConfig profiles, DeviceIntegrationConfig device)
    {
        HashSet<string> authored = new(device.DeviceProfiles.SelectMany(scope => scope.Profiles)
            .Select(profile => profile.ProfileId), StringComparer.Ordinal);
        HashSet<string> lighting = new(device.DeviceProfiles.SelectMany(scope => scope.Profiles)
            .Where(profile => profile.CapabilityId == CapabilityIds.LightingColor)
            .Select(profile => profile.ProfileId), StringComparer.Ordinal);
        profiles.Global ??= new ProfileValues();
        NormalizeProfileValues(profiles.Global, authored, lighting);
        profiles.Games ??= [];
        profiles.Games.RemoveAll(static game => game is null || string.IsNullOrWhiteSpace(game.Id));
        HashSet<string> ids = new(StringComparer.Ordinal);
        profiles.Games.RemoveAll(game => !ids.Add(game.Id.Trim()));
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var game in profiles.Games)
        {
            game.Id = game.Id.Trim();
            game.Name = game.Name?.Trim() ?? string.Empty;
            // One executable activates one profile. A second claim would make the match ambiguous,
            // and an ambiguous match resolves to no profile at all.
            game.ProcessNames = (game.ProcessNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                               && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(claimed.Add)
                .ToList();
            // Learned names match nothing, so two games may share one; only the shape is checked.
            game.Executables = (game.Executables ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                               && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            game.Values ??= new ProfileValues();
            NormalizeProfileValues(game.Values, authored, lighting);
        }

        return [];
    }

    private static void NormalizeProfileValues(ProfileValues values, HashSet<string> authoredProfiles,
        HashSet<string> lightingProfiles)
    {
        values.AcPowerPreset = NormalizePowerPreset(values.AcPowerPreset);
        values.BatteryPowerPreset = NormalizePowerPreset(values.BatteryPowerPreset);
        if (values.ControllerTarget is { } target && !Enum.IsDefined(target))
        {
            values.ControllerTarget = null;
        }

        if (values.FanCurveProfileId is { } fanCurve)
        {
            values.FanCurveProfileId = authoredProfiles.Contains(fanCurve.Trim()) ? fanCurve.Trim() : null;
        }

        if (values.LightingProfileId is { } lighting)
        {
            values.LightingProfileId = lightingProfiles.Contains(lighting.Trim()) ? lighting.Trim() : null;
        }

        values.Device ??= [];
        values.Device.RemoveAll(static entry => entry?.Value is null
                                                || string.IsNullOrWhiteSpace(entry.DeviceIdentityKey)
                                                || string.IsNullOrWhiteSpace(entry.CapabilityId)
                                                || entry.Value.Kind is CapabilityValueKind.None);
        foreach (var entry in values.Device)
        {
            entry.DeviceIdentityKey = entry.DeviceIdentityKey.Trim();
            entry.CapabilityId = entry.CapabilityId.Trim();
            entry.InstanceId = string.IsNullOrWhiteSpace(entry.InstanceId) ? null : entry.InstanceId.Trim();
            if (entry.Value is { Kind: CapabilityValueKind.Color, ColorValue: { } color })
            {
                // The picker hands back an alpha channel WSGM never uses, and a stored alpha byte reads
                // as a different colour when the value is unpacked as RGB.
                entry.Value = entry.Value with { ColorValue = color & 0xFFFFFF };
            }
        }

        // The resolver takes the first entry for a key, so a duplicate would decide the value by
        // file order.
        HashSet<(string, string, string?)> keys = [];
        values.Device.RemoveAll(entry =>
            !keys.Add((entry.DeviceIdentityKey, entry.CapabilityId, entry.InstanceId)));
    }

    private static DevicePowerPresetReference? NormalizePowerPreset(DevicePowerPresetReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var pluginId = reference.PluginId?.Trim() ?? string.Empty;
        var presetId = reference.PresetId?.Trim() ?? string.Empty;
        if (pluginId.Length is 0 || presetId.Length is 0)
        {
            return null;
        }

        reference.PluginId = pluginId;
        reference.PresetId = presetId;
        if (presetId == "custom")
        {
            if (reference.CustomValues is not { SustainedWatts: > 0 } values
                || values.SlowWatts < values.SustainedWatts || !Enum.IsDefined(values.WindowsMode)
                || values.Scenario is { Length: 0 })
            {
                return null;
            }
        }
        else
        {
            reference.CustomValues = null;
        }

        return reference;
    }
}
