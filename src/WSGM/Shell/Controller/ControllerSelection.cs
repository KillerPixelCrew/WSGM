using System;
using WSGM.Core;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

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
/// <param name="NativeTarget">
///     The target the physical pad already is (the Steam Deck controller on a Deck), or null. Choosing it
///     leaves the pad to Steam: WSGM creates no virtual pad and hides nothing.
/// </param>
internal sealed record ControllerSelection(
    bool Enabled,
    ProfileConfig Profiles,
    string DisabledDetail,
    ManagedControllerTarget? NativeTarget = null)
{
    /// <summary>Whether the resolved target is the physical pad itself.</summary>
    /// <param name="resolved">The target resolved for the running application.</param>
    /// <returns>True when management is on and the target is the native one.</returns>
    internal bool IsNative(ResolvedControllerTarget resolved)
    {
        return Enabled && NativeTarget is { } native && resolved.Target == native;
    }

    /// <summary>Projects stored settings and the matched definition through the release gate.</summary>
    /// <param name="config">The stored device-integration configuration.</param>
    /// <param name="profiles">The profile store.</param>
    /// <param name="definition">The matched definition, or null when none is running.</param>
    /// <returns>The selection in effect; off for a definition that provides hardware controls only.</returns>
    internal static ControllerSelection From(DeviceIntegrationConfig config, ProfileConfig profiles,
        HandheldDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(profiles);
        var nativeTarget = DeviceCoordinator.NativeTarget(definition);
        if (definition is { HasController: false })
        {
            return new ControllerSelection(false, profiles, "This device definition provides hardware controls only.",
                nativeTarget);
        }

        var enabled = config is { Enabled: true, ControllerManagementEnabled: true };
        var detail = enabled ? string.Empty : "Controller management is off.";
        return new ControllerSelection(enabled, profiles, detail, nativeTarget);
    }
}
