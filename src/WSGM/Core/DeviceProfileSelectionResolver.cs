using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which authored profile is in force, and where the choice came from.</summary>
/// <param name="Profile">The profile to apply, or null when none is in force.</param>
/// <param name="ApplicationScoped">
/// Whether an application override supplied it rather than the global choice.
/// </param>
/// <param name="Diagnostic">
/// Why nothing is in force, or why a selection was ignored. Null when a profile resolved cleanly.
/// </param>
public readonly record struct DeviceProfileResolution(
    DeviceAuthoredProfile? Profile,
    bool ApplicationScoped,
    string? Diagnostic);

/// <summary>
/// Resolves which authored profile applies to the running application.
/// </summary>
/// <remarks>
/// Pure, and the same precedence as the semantic capability layers it sits beside: an application
/// override outranks the global choice, and no choice at all leaves the capability alone rather than
/// inventing one.
/// <para>
/// Selections reference a profile by id, so this is also where a reference to a profile the user has
/// since deleted is caught. It resolves to nothing and says so, because applying a stale profile
/// would be worse than applying none and silently applying none is what makes it undiagnosable.
/// </para>
/// </remarks>
public static class DeviceProfileSelectionResolver
{
    /// <summary>Resolves the profile in force.</summary>
    /// <param name="selections">Selections stored for the device.</param>
    /// <param name="profiles">Profiles authored for the device.</param>
    /// <param name="capabilityId">The capability being resolved.</param>
    /// <param name="applicationId">
    /// The canonical running-application identity, or null when none is running.
    /// </param>
    /// <returns>The profile to apply and where the choice came from.</returns>
    public static DeviceProfileResolution Resolve(
        IReadOnlyList<DeviceProfileSelection> selections,
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string capabilityId,
        string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(profiles);

        DeviceProfileSelection? selection = selections.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, capabilityId, StringComparison.Ordinal));
        if (selection is null)
        {
            return new DeviceProfileResolution(null, false, null);
        }

        if (applicationId is { Length: > 0 })
        {
            DeviceApplicationProfileSelection? overridden = selection.ApplicationOverrides
                .FirstOrDefault(candidate => string.Equals(
                    candidate.ApplicationId,
                    applicationId,
                    StringComparison.Ordinal));
            if (overridden is not null)
            {
                return Find(profiles, overridden.ProfileId, applicationScoped: true, applicationId);
            }
        }

        return selection.GlobalProfileId is { Length: > 0 } global
            ? Find(profiles, global, applicationScoped: false, applicationId: null)
            : new DeviceProfileResolution(null, false, null);
    }

    private static DeviceProfileResolution Find(
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string profileId,
        bool applicationScoped,
        string? applicationId)
    {
        DeviceAuthoredProfile? profile = profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal));
        if (profile is not null)
        {
            return new DeviceProfileResolution(profile, applicationScoped, null);
        }

        // Named, and never silently downgraded to the global choice. A per-application selection
        // pointing at a deleted profile means the user's intent for that application is gone, and
        // quietly running the global profile instead hides it.
        return new DeviceProfileResolution(
            null,
            applicationScoped,
            applicationScoped
                ? $"application '{applicationId}' selects profile '{profileId}', which no longer exists"
                : $"the global selection names profile '{profileId}', which no longer exists");
    }
}
