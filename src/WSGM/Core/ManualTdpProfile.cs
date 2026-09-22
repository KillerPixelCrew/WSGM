namespace WSGM.Core;

/// <summary>Manual power preferences retained independently for unified and advanced controls.</summary>
/// <param name="Unified">Whether the coordinated plugin target is selected.</param>
/// <param name="UnifiedWatts">Explicit coordinated target, or null when none has been selected.</param>
/// <param name="SustainedWatts">Saved advanced sustained limit, retained while unified mode is selected.</param>
/// <param name="BoostWatts">Saved advanced boost limit, retained while unified mode is selected.</param>
/// <remarks>
///     These are user preferences, not hardware readback. Changing mode alone neither invents
///     values nor requests a device write. The active plugin validates values against current bounds.
/// </remarks>
public sealed record ManualTdpProfile(bool Unified, int? UnifiedWatts, int? SustainedWatts, int? BoostWatts);

/// <summary>Resolves the selected manual profile without deriving preferences from readback.</summary>
internal static class ManualTdpPolicy
{
    internal static bool Accepts(int? minimum, int? maximum, int? step, int watts)
    {
        return minimum is { } min && maximum is { } max && step is > 0
               && watts >= min && watts <= max && ((long)watts - min) % step.Value == 0;
    }

    /// <summary>The manual power target in force and whether it is the coordinated pair.</summary>
    /// <param name="layers">The profile layers for the running application.</param>
    /// <returns>The target watts, or null when no layer sets one.</returns>
    internal static (int? Watts, bool Paired) ResolveTarget(ProfileLayers layers)
    {
        return layers.ManualTdp() is { } profile
            ? profile.Unified ? (profile.UnifiedWatts, true) : (profile.SustainedWatts, false)
            : (null, false);
    }
}
