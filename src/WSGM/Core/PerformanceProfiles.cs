using System.Linq;

namespace WSGM.Core;

/// <summary>Which layer supplied a performance value.</summary>
public enum PerformanceProfileSource
{
    /// <summary>The global profile, because no application profile is in effect.</summary>
    Global,

    /// <summary>The running application's own profile.</summary>
    Application,
}

/// <summary>The performance settings actually in force right now.</summary>
/// <param name="Source">Which layer supplied them.</param>
/// <param name="ApplicationId">The application they belong to, empty under the global profile.</param>
/// <param name="FrameLimit">Frame cap in FPS; zero or null means uncapped.</param>
/// <param name="OverlayLevel">Performance-overlay level, or null to leave it alone.</param>
/// <param name="TdpWatts">Sustained power limit in watts, or null to leave it to the device.</param>
/// <param name="VariableRefreshRate">Variable refresh preference, or null to leave the panel alone.</param>
public readonly record struct EffectivePerformanceProfile(
    PerformanceProfileSource Source,
    string ApplicationId,
    int? FrameLimit,
    int? OverlayLevel,
    int? TdpWatts,
    bool? VariableRefreshRate
);

/// <summary>
/// Resolves the global and per-application performance layers into the values in force.
/// </summary>
/// <remarks>
/// This is the model Steam's own performance panel is built around: one global profile, an optional
/// per-game profile, and a switch that decides which is editing. Keeping the resolution here — pure,
/// and separate from both the overlay and the Steam adapter — is what lets the same answer drive
/// both surfaces without a second projection.
/// </remarks>
public static class PerformanceProfiles
{
    /// <summary>
    /// The values in force for an application.
    /// </summary>
    /// <param name="config">The performance configuration.</param>
    /// <param name="applicationId">Canonical identity of the running application, if any.</param>
    /// <returns>The effective profile, and which layer supplied it.</returns>
    /// <remarks>
    /// An application profile applies only when the user has switched it on. A stored value with the
    /// switch off is deliberately still returned as the global value: the point of the switch is
    /// that it is reversible without the user setting everything up again.
    /// </remarks>
    public static EffectivePerformanceProfile Resolve(
        PerformanceConfig config,
        string? applicationId
    )
    {
        PerformanceApplicationConfig? application = Find(config, applicationId);
        if (application is null || !application.UsePerGameProfile)
        {
            return new EffectivePerformanceProfile(
                PerformanceProfileSource.Global,
                string.Empty,
                config.FrameLimit,
                config.OverlayLevel,
                config.TdpWatts,
                config.VariableRefreshRate);
        }

        // Each value falls back independently, so a game profile that only pins a frame cap still
        // follows the global power limit rather than silently clearing it.
        return new EffectivePerformanceProfile(
            PerformanceProfileSource.Application,
            application.ApplicationId,
            application.FrameLimit ?? config.FrameLimit,
            application.OverlayLevel ?? config.OverlayLevel,
            application.TdpWatts ?? config.TdpWatts,
            application.VariableRefreshRate ?? config.VariableRefreshRate);
    }

    /// <summary>
    /// Whether the running application has its own profile switched on.
    /// </summary>
    /// <param name="config">The performance configuration.</param>
    /// <param name="applicationId">Canonical identity of the running application, if any.</param>
    /// <returns><see langword="true"/> when an application profile is editing.</returns>
    public static bool UsesApplicationProfile(PerformanceConfig config, string? applicationId) =>
        Find(config, applicationId)?.UsePerGameProfile ?? false;

    /// <summary>
    /// Turns an application profile on or off, creating the entry the first time.
    /// </summary>
    /// <param name="config">The performance configuration to change.</param>
    /// <param name="applicationId">Canonical identity of the running application.</param>
    /// <param name="enabled">Whether the application's own values should apply.</param>
    /// <returns>The entry, or null when there is no application to attach it to.</returns>
    public static PerformanceApplicationConfig? SetApplicationProfileEnabled(
        PerformanceConfig config,
        string? applicationId,
        bool enabled
    )
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            Log.Warn("Performance profile: per-game switch ignored; no running application.");
            return null;
        }

        PerformanceApplicationConfig? application = Find(config, applicationId);
        if (application is null)
        {
            application = new PerformanceApplicationConfig
            {
                ApplicationId = applicationId.Trim(),
            };
            config.Applications.Add(application);
        }

        application.UsePerGameProfile = enabled;
        Log.Info(
            $"Performance profile: '{application.ApplicationId}' per-game profile "
            + (enabled ? "on." : "off; stored values retained."));
        return application;
    }

    private static PerformanceApplicationConfig? Find(
        PerformanceConfig config,
        string? applicationId
    ) =>
        string.IsNullOrWhiteSpace(applicationId)
            ? null
            : config.Applications.FirstOrDefault(entry =>
                string.Equals(entry.ApplicationId, applicationId, System.StringComparison.Ordinal));
}
