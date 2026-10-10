using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Repairs authored device profiles, glyph selection and OEM assignments without opening hardware.</summary>
internal static class DeviceConfigurationRules
{
    /// <summary>
    ///     Brings device-integration configuration into a shape the rest of WSGM can rely on.
    /// </summary>
    /// <param name="device">The section to normalize in place.</param>
    /// <remarks>
    ///     Performs structural repair only; live capability bounds are validated separately against
    ///     the active device descriptor. No files or hardware are read.
    /// </remarks>
    /// <returns>Diagnostics for discarded authored curves; the supplied configuration is mutated in place.</returns>
    internal static IReadOnlyList<string> Normalize(DeviceIntegrationConfig device)
    {
        List<string> diagnostics = [];
        device.ManualGlyphProfileId = string.IsNullOrWhiteSpace(device.ManualGlyphProfileId)
            ? null
            : device.ManualGlyphProfileId.Trim();
        device.OemAssignments ??= [];
        if (device.PreferencesSchemaVersion != DeviceIntegrationConfig.CurrentPreferencesSchemaVersion)
        {
            device.OemAssignments.Clear();
            device.PreferencesSchemaVersion = DeviceIntegrationConfig.CurrentPreferencesSchemaVersion;
        }

        device.DeviceProfiles ??= [];
        device.DeviceProfiles.RemoveAll(static scope => scope is null
                                                        || string.IsNullOrWhiteSpace(scope.DeviceDefinitionId)
                                                        || string.IsNullOrWhiteSpace(scope.FamilyId));
        foreach (var scope in device.DeviceProfiles)
        {
            scope.DeviceDefinitionId = scope.DeviceDefinitionId.Trim();
            scope.FamilyId = scope.FamilyId.Trim();
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
        }

        HashSet<(string DeviceDefinitionId, string FamilyId)> scopeKeys = [];
        device.DeviceProfiles.RemoveAll(scope => !scopeKeys.Add((scope.DeviceDefinitionId, scope.FamilyId)));
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
