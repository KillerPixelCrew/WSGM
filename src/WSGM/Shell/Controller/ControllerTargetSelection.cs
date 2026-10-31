using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Resolves the managed-controller target for the running application.</summary>
/// <remarks>
///     Matched with the same rule as every other per-game value, against the same canonical identity
///     and executable, so the controller target and the rest of the profile always agree about which
///     game is running.
/// </remarks>
internal static class ControllerTargetSelection
{
    /// <summary>The target used when no profile layer sets one.</summary>
    internal const ManagedControllerTarget Default = ProfileFields.DefaultControllerTarget;

    /// <summary>Resolves the target for the running application.</summary>
    /// <param name="profiles">The profile store.</param>
    /// <param name="applicationId">Canonical identity of the running application, when one is known.</param>
    /// <param name="executable">Its executable, when known.</param>
    /// <returns>The effective target and the layer that supplied it.</returns>
    internal static ResolvedControllerTarget Resolve(
        ProfileConfig profiles,
        string? applicationId,
        string? executable)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var active = ProfileResolver.Activate(profiles, applicationId, executable, null);
        var resolved = ProfileResolver.Layers(profiles, active).Value(values => values.ControllerTarget);
        return new ResolvedControllerTarget(
            resolved.Value ?? Default,
            resolved.Source,
            resolved.Source is ProfileSource.Game ? applicationId : null);
    }
}
