using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The complete stored controller-management selection, projected through the release gate.
/// </summary>
/// <remarks>
///     The management switches come from Device Integration; the target itself is an ordinary profile
///     value, so it follows the same Game, then Global, rule as everything else. The compile-time release
///     gate belongs in this projection rather than inside <see cref="ControllerManager" />: the manager's
///     behaviour with management enabled has to stay testable while the shipped gate is closed.
/// </remarks>
/// <param name="Enabled">Whether controller management may run at all.</param>
/// <param name="Profiles">The profile store the target resolves from.</param>
/// <param name="DisabledDetail">Why management is off, when it is.</param>
internal sealed record ControllerSelection(
    bool Enabled,
    ProfileConfig Profiles,
    string DisabledDetail)
{
    /// <summary>Projects stored settings through the release gate.</summary>
    /// <param name="config">The stored device-integration configuration.</param>
    /// <param name="profiles">The profile store.</param>
    /// <returns>The selection in effect.</returns>
    internal static ControllerSelection From(DeviceIntegrationConfig config, ProfileConfig profiles)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(profiles);
        var enabled = config is { Enabled: true, ControllerManagementEnabled: true };
        var detail = enabled ? string.Empty : "Controller management is off.";
        return new ControllerSelection(enabled, profiles, detail);
    }
}

/// <summary>The managed-controller target in effect and where it came from.</summary>
internal sealed record ResolvedControllerTarget(
    ManagedControllerTarget Target,
    ProfileSource Source,
    string? ApplicationId);

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
