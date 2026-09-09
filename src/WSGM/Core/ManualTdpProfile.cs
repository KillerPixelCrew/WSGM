namespace WSGM.Core;

/// <summary>Manual power preferences retained independently for unified and advanced controls.</summary>
/// <param name="Unified">Whether the coordinated plugin target is selected.</param>
/// <param name="UnifiedWatts">Explicit coordinated target, or null when none has been selected.</param>
/// <param name="SustainedWatts">Saved advanced sustained limit, retained while unified mode is selected.</param>
/// <param name="BoostWatts">Saved advanced boost limit, retained while unified mode is selected.</param>
/// <remarks>These are user preferences, not hardware readback. Changing mode alone neither invents
/// values nor requests a device write. The active plugin validates values against current bounds.</remarks>
public sealed record ManualTdpProfile(bool Unified, int? UnifiedWatts, int? SustainedWatts, int? BoostWatts);

/// <summary>Resolves the selected manual profile without deriving preferences from readback.</summary>
internal static class ManualTdpPolicy
{
    internal static ManualTdpProfile? Resolve(PerformanceConfig global, PerformanceApplicationConfig? application,
        bool perGameActive) => perGameActive && application?.ManualTdp is { } own ? own : global.ManualTdp;

    internal static (int? Watts, bool Paired) ResolveTarget(PerformanceConfig global,
        PerformanceApplicationConfig? application, bool perGameActive)
    {
        var profile = Resolve(global, application, perGameActive);
        if (profile is not null)
        {
            return profile.Unified ? (profile.UnifiedWatts, true) : (profile.SustainedWatts, false);
        }
        return (PerApplicationPowerPolicy.ResolveEffective(global.TdpWatts, application?.TdpWatts, perGameActive), false);
    }
}
