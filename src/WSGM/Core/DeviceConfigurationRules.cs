using System;
using System.Collections.Generic;

namespace WSGM.Core;

internal static class DeviceConfigurationRules
{
    /// <summary>
    ///     Brings device-integration configuration into a shape the rest of WSGM can rely on.
    /// </summary>
    /// <param name="device">The section to normalize in place.</param>
    /// <remarks>
    ///     Internal rather than private so its rules can be tested directly. It touches only the object
    ///     handed to it and reads no file, which is what keeps a test off the developer's real
    ///     configuration.
    /// </remarks>
    internal static IReadOnlyList<string> Normalize(DeviceIntegrationConfig device)
    {
        List<string> diagnostics = [];
        ConfigRepair.NormalizeEnums(device);
        device.ManualGlyphProfileId = string.IsNullOrWhiteSpace(device.ManualGlyphProfileId)
            ? null
            : device.ManualGlyphProfileId.Trim();
        // A scope with no device, no plugin, or no values keys nothing and can never be matched, so
        // it would sit in the file forever growing it. Values are only shape-checked here; whether
        // one still satisfies its declared bounds is decided against the live manifest on load,
        // because a plugin update can narrow a range after the value was stored.
        device.PluginSettings ??= [];
        device.PluginSettings.RemoveAll(static scope => scope is null
                                                        || string.IsNullOrWhiteSpace(scope.DeviceDefinitionId)
                                                        || string.IsNullOrWhiteSpace(scope.PluginId));
        foreach (var scope in device.PluginSettings)
        {
            scope.DeviceDefinitionId = scope.DeviceDefinitionId.Trim();
            scope.PluginId = scope.PluginId.Trim();

            // Settings renders the cached declaration without activating plugin code. Drop malformed
            // declarations so every rendered control has the bounds required by the SDK contract.
            if (scope.Declaration is { } declaration && !declaration.TryValidate(out var reason))
            {
                diagnostics.Add(
                    $"Plugin settings: cached declaration for {scope.PluginId} on "
                    + $"{scope.DeviceDefinitionId} was dropped: {reason}");
                scope.Declaration = null;
            }

            // A profile with no id keys nothing and can never be selected, and one whose curve is
            // not strictly ascending is refused by the device router on apply — keeping either
            // would leave the user a profile that silently does nothing when chosen.
            scope.Profiles ??= [];
            scope.Profiles.RemoveAll(profile => profile is null
                                                       || string.IsNullOrWhiteSpace(profile.ProfileId)
                                                       || string.IsNullOrWhiteSpace(profile.CapabilityId));
            HashSet<string> profileIds = new(StringComparer.Ordinal);
            scope.Profiles.RemoveAll(profile => !profileIds.Add(profile.ProfileId.Trim()));
            foreach (var profile in scope.Profiles)
            {
                profile.ProfileId = profile.ProfileId.Trim();
                profile.CapabilityId = profile.CapabilityId.Trim();
                profile.Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? profile.ProfileId
                    : profile.Name.Trim();
                profile.Curve ??= [];
                profile.Curve.RemoveAll(static point => point is null);
            }

            scope.Profiles.RemoveAll(profile =>
            {
                if (profile.Curve.Count == 0)
                {
                    return false;
                }

                for (var index = 1; index < profile.Curve.Count; index++)
                {
                    if (profile.Curve[index].Input > profile.Curve[index - 1].Input)
                    {
                        continue;
                    }

                    diagnostics.Add(
                        $"Device profile '{profile.ProfileId}' was dropped: its curve inputs "
                        + "are not strictly ascending.");
                    return true;
                }

                return false;
            });
            scope.Values ??= [];
            scope.Values.RemoveAll(static value => value is null
                                                   || string.IsNullOrWhiteSpace(value.SettingId));
            HashSet<string> settingIds = new(StringComparer.Ordinal);
            scope.Values.RemoveAll(value => !settingIds.Add(value.SettingId.Trim()));
            foreach (var value in scope.Values)
            {
                value.SettingId = value.SettingId.Trim();
            }
        }

        HashSet<(string DeviceDefinitionId, string PluginId)> scopeKeys = [];
        device.PluginSettings.RemoveAll(scope => !scopeKeys.Add((scope.DeviceDefinitionId, scope.PluginId)));

        device.OemAssignments ??= [];
        device.OemAssignments.RemoveAll(static assignment => assignment is null
                                                             || string.IsNullOrWhiteSpace(assignment.ControlId)
                                                             || !Enum.IsDefined(assignment.Action));
        HashSet<string> controls = new(StringComparer.Ordinal);
        device.OemAssignments.RemoveAll(assignment => !controls.Add(assignment.ControlId.Trim()));
        foreach (var assignment in device.OemAssignments)
        {
            assignment.ControlId = assignment.ControlId.Trim();
        }
        return diagnostics;
    }
}
