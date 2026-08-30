using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Which scope a profile choice applies to.</summary>
public enum DeviceProfileScope
{
    /// <summary>Everything without an override of its own.</summary>
    Global,

    /// <summary>Only the application that is running now.</summary>
    Application,
}

/// <summary>
/// Reads and writes which authored profile is in force, for the overlay to drive.
/// </summary>
/// <remarks>
/// Selection is the overlay's job and authoring is Settings' (D22b): this writes only which profile
/// is chosen and never the profile's contents, so the two surfaces cannot fight over one record.
/// <para>
/// Every write goes through the caller's own configuration mutation, so this holds no state and the
/// cross-process config lock stays owned by <c>ConfigStore</c> rather than being taken twice.
/// </para>
/// </remarks>
public static class DeviceProfileSelectionStore
{
    /// <summary>Reads the profile id currently chosen for a capability.</summary>
    /// <param name="scope">The device scope holding the selections.</param>
    /// <param name="capabilityId">The capability being read.</param>
    /// <param name="applicationId">The running application, or null for none.</param>
    /// <param name="applicationScoped">
    /// Whether the answer came from an application override rather than the global choice.
    /// </param>
    /// <returns>The chosen profile id, or null when nothing is chosen.</returns>
    public static string? ReadSelection(
        PluginSettingsScope scope,
        string capabilityId,
        string? applicationId,
        out bool applicationScoped)
    {
        ArgumentNullException.ThrowIfNull(scope);
        applicationScoped = false;
        DeviceProfileSelection? selection = Find(scope, capabilityId);
        if (selection is null)
        {
            return null;
        }

        if (applicationId is { Length: > 0 })
        {
            DeviceApplicationProfileSelection? overridden = selection.ApplicationOverrides
                .FirstOrDefault(entry => string.Equals(
                    entry.ApplicationId,
                    applicationId,
                    StringComparison.Ordinal));
            if (overridden is not null)
            {
                applicationScoped = true;
                return overridden.ProfileId;
            }
        }

        return selection.GlobalProfileId;
    }

    /// <summary>Chooses a profile, or clears the choice.</summary>
    /// <param name="scope">The device scope to write into.</param>
    /// <param name="capabilityId">The capability being set.</param>
    /// <param name="profileId">The profile to choose, or null to clear.</param>
    /// <param name="target">Whether this is the global choice or an application override.</param>
    /// <param name="applicationId">
    /// The application the override belongs to. Required for
    /// <see cref="DeviceProfileScope.Application"/>.
    /// </param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// Clearing an application override falls back to the global choice, which is the difference
    /// between "this game uses the default" and "this game uses nothing" — the first is what a user
    /// clearing an override means, and there is no way to express the second on purpose.
    /// </remarks>
    public static bool SetSelection(
        PluginSettingsScope scope,
        string capabilityId,
        string? profileId,
        DeviceProfileScope target,
        string? applicationId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return false;
        }

        if (target is DeviceProfileScope.Application && string.IsNullOrWhiteSpace(applicationId))
        {
            // Refused rather than quietly written as the global choice: silently widening a
            // per-game change to every game is the worst possible reading of the user's intent.
            Log.Warn(
                $"Device profile selection for '{capabilityId}' was refused: an application-scoped "
                + "choice needs a running application.");
            return false;
        }

        DeviceProfileSelection? selection = Find(scope, capabilityId);
        if (selection is null)
        {
            if (profileId is null)
            {
                return false;
            }

            selection = new DeviceProfileSelection { CapabilityId = capabilityId };
            scope.ProfileSelections.Add(selection);
        }

        if (target is DeviceProfileScope.Global)
        {
            if (string.Equals(selection.GlobalProfileId, profileId, StringComparison.Ordinal))
            {
                return false;
            }

            selection.GlobalProfileId = profileId;
            return true;
        }

        List<DeviceApplicationProfileSelection> overrides = selection.ApplicationOverrides;
        DeviceApplicationProfileSelection? existing = overrides.FirstOrDefault(entry =>
            string.Equals(entry.ApplicationId, applicationId, StringComparison.Ordinal));

        if (profileId is null)
        {
            return existing is not null && overrides.Remove(existing);
        }

        if (existing is not null)
        {
            if (string.Equals(existing.ProfileId, profileId, StringComparison.Ordinal))
            {
                return false;
            }

            existing.ProfileId = profileId;
            return true;
        }

        overrides.Add(new DeviceApplicationProfileSelection
        {
            ApplicationId = applicationId!,
            ProfileId = profileId,
        });
        return true;
    }

    private static DeviceProfileSelection? Find(PluginSettingsScope scope, string capabilityId) =>
        scope.ProfileSelections.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, capabilityId, StringComparison.Ordinal));
}
