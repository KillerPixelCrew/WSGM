namespace WSGM.Core;

/// <summary>What to do with the device power limit when the running application changes.</summary>
internal enum PerAppPowerAction
{
    /// <summary>Write a concrete watt value the resolved layer specifies.</summary>
    Apply,

    /// <summary>Hand control back to AutoTDP, which the outgoing application's own limit had paused.</summary>
    ResumeAutomatic,

    /// <summary>Release the limit to the device ceiling: nothing is preferred and AutoTDP is off.</summary>
    ReleaseToCeiling,

    /// <summary>Change nothing. WSGM never imposed a limit, so it has none to take back.</summary>
    Leave
}

/// <summary>One resolved power decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Watts">
///     The value for <see cref="PerAppPowerAction.Apply" /> and
///     <see cref="PerAppPowerAction.ReleaseToCeiling" />; ignored otherwise.
/// </param>
internal readonly record struct PerAppPowerDecision(PerAppPowerAction Action, int Watts);

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

/// <summary>What to do with the variable-refresh state when the running application changes.</summary>
internal enum PerAppVrrAction
{
    /// <summary>Write a concrete on/off state.</summary>
    Apply,

    /// <summary>Change nothing. WSGM never set a state, so it has none to take back.</summary>
    Leave
}

/// <summary>One resolved variable-refresh decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Enabled">The state to write for <see cref="PerAppVrrAction.Apply" />.</param>
internal readonly record struct PerAppVrrDecision(PerAppVrrAction Action, bool Enabled);

/// <summary>The action to take on the processor boost mode for a transition.</summary>
internal enum PerAppCpuBoostAction
{
    /// <summary>Write the carried mode.</summary>
    Apply,

    /// <summary>Change nothing.</summary>
    Leave
}

/// <summary>What the transition does to the processor boost mode.</summary>
/// <param name="Action">The action.</param>
/// <param name="Mode">The mode to write, for <see cref="PerAppCpuBoostAction.Apply" />.</param>
internal readonly record struct PerAppCpuBoostDecision(PerAppCpuBoostAction Action, CpuBoostMode Mode);

/// <summary>Decides what a running-application change does to the processor boost mode.</summary>
/// <remarks>
///     Like the display twin, with one difference in the restore baseline: Windows has no default the
///     way a fixed-refresh desktop does, so when no layer prefers a mode and WSGM had imposed one, the
///     mode WSGM found before its first write comes back. An unknown baseline is left alone rather
///     than replaced with a guess.
/// </remarks>
internal static class PerApplicationCpuBoostPolicy
{
    /// <summary>Decides the action for a transition to the resolved mode.</summary>
    /// <param name="effectiveMode">The mode resolved for the new application, or null for none.</param>
    /// <param name="modeCurrentlyImposed">Whether WSGM's per-application feature is the reason the current mode holds.</param>
    /// <param name="baseline">The mode WSGM observed before it first wrote one, or null when unknown.</param>
    /// <returns>The action and, where relevant, the mode it carries.</returns>
    internal static PerAppCpuBoostDecision DecideOnTargetChange(
        CpuBoostMode? effectiveMode,
        bool modeCurrentlyImposed,
        CpuBoostMode? baseline)
    {
        return effectiveMode is { } mode
            ? new PerAppCpuBoostDecision(PerAppCpuBoostAction.Apply, mode)
            : modeCurrentlyImposed && baseline is { } original
                ? new PerAppCpuBoostDecision(PerAppCpuBoostAction.Apply, original)
                : new PerAppCpuBoostDecision(PerAppCpuBoostAction.Leave, default);
    }
}

/// <summary>
///     Pure per-application variable-refresh policy, the display twin of
///     <see cref="PerApplicationPowerPolicy" />: which layer owns the VRR state and what a transition must
///     do so a state set for one game does not leak onto the next application or the desktop.
/// </summary>
/// <remarks>
///     Simpler than the power twin because there is no automatic controller to coordinate with. The one
///     judgement it makes is the restore baseline: when no state is preferred but WSGM had set one,
///     variable refresh returns to off — the state Steam's own model treats as the default and the one a
///     fixed-refresh desktop expects — rather than being left on because a game enabled it.
/// </remarks>
internal static class PerApplicationVrrPolicy
{
    /// <summary>Decides the display action for a transition to the resolved state.</summary>
    /// <param name="effectiveState">The state resolved for the new application, or null for none.</param>
    /// <param name="stateCurrentlyImposed">
    ///     Whether WSGM's per-application feature is the reason variable refresh currently holds a state.
    ///     Only then is there something to take back to the default.
    /// </param>
    /// <returns>The action and, where relevant, the state it carries.</returns>
    internal static PerAppVrrDecision DecideOnTargetChange(
        bool? effectiveState,
        bool stateCurrentlyImposed)
    {
        return effectiveState is { } state
            ? new PerAppVrrDecision(PerAppVrrAction.Apply, state)
            : stateCurrentlyImposed
                ? new PerAppVrrDecision(PerAppVrrAction.Apply, false)
                : new PerAppVrrDecision(PerAppVrrAction.Leave, false);
    }
}
