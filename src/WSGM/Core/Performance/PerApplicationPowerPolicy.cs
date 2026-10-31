namespace WSGM.Core;

/// <summary>
///     Pure per-application power-limit policy: which layer owns the limit, and what a transition to a
///     new application must do to the device so a limit set for one game does not leak onto the next
///     application or the desktop.
/// </summary>
/// <remarks>
///     The bug this exists to prevent: a limit set inside a game stayed on the device after the game
///     closed, silently becoming the global limit. The profile store resolves which layer holds the
///     limit; this decides what to do with that value, and crucially what to do when
///     the resolved value is <em>absent</em> — which is where the leak happened, because "no preference"
///     was read as "keep whatever is currently on the device".
/// </remarks>
internal static class PerApplicationPowerPolicy
{
    /// <summary>Decides the device action for a transition to the resolved limit.</summary>
    /// <param name="effectiveWatts">The limit resolved for the new application, or null for none.</param>
    /// <param name="powerCurrentlyImposed">
    ///     Whether WSGM's per-application feature is the reason the device currently holds a limit. Only
    ///     then is there something to take back: a limit WSGM never set is not WSGM's to release.
    /// </param>
    /// <param name="autoTdpEnabled">Whether automatic control is switched on.</param>
    /// <param name="ceilingWatts">The device's maximum limit, used only for a release.</param>
    /// <returns>The action and, where relevant, the watts it carries.</returns>
    /// <remarks>
    ///     A concrete preference is always applied, whether it came from the application or the global
    ///     layer, because an explicit limit overrides automatic control the same way moving the slider
    ///     does. When no preference exists, the outgoing application's limit is undone rather than left
    ///     on the device: automatic control resumes if it is on, and otherwise the limit is released to
    ///     the ceiling — but only when WSGM actually imposed the current one, so a session that never
    ///     used the feature is never touched.
    /// </remarks>
    internal static PerAppPowerDecision DecideOnTargetChange(
        int? effectiveWatts,
        bool powerCurrentlyImposed,
        bool autoTdpEnabled,
        int ceilingWatts)
    {
        if (effectiveWatts is { } watts)
        {
            return new PerAppPowerDecision(PerAppPowerAction.Apply, watts);
        }

        if (!powerCurrentlyImposed)
        {
            return new PerAppPowerDecision(PerAppPowerAction.Leave, 0);
        }

        return autoTdpEnabled
            ? new PerAppPowerDecision(PerAppPowerAction.ResumeAutomatic, 0)
            : new PerAppPowerDecision(PerAppPowerAction.ReleaseToCeiling, ceilingWatts);
    }
}
