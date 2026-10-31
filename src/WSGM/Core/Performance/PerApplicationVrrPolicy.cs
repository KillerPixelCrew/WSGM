namespace WSGM.Core;

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
